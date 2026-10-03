using System;
using System.Collections.Generic;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Transition assertion for refused bound-object operations (review item FOLLOWUP 6, written before
    /// verification): "a refused reader/enumerator operation leaves the transaction Active and it can
    /// still commit its prior writes". A's handle writes, opens a bound enumerator or data reader and
    /// reads one document. In the overlap variants C tries to dispose that reader while A is paused
    /// inside another bound call (an overlapping call, refused before executing). Then the closer (A,
    /// or B by sequential handoff) disposes the reader, and C uses the disposed reader once more
    /// (advance, Current, or a second Dispose). Whatever that late call observes (success, a disposed
    /// or a refusal), the handle must still be Active afterwards, B's Commit must succeed, and the
    /// handle's prior writes must be in the cold-reopened file.
    /// </summary>
    internal sealed class HandleDisposedBoundObjectScenario : IExplorerScenario
    {
        private static readonly string[] LateOps = { "advance", "current", "dispose" };

        public string Name => "handle-disposed-bound-object";
        public string Description => "A disposed bound reader or enumerator is used again; the handle stays Active and commits its writes.";
        public int Variants => 2 * 3 * 2 * 2;

        public string NotApplicable(ExplorerConfiguration configuration, IExplorerAccess access) => HandleChecks.OnlyHandle(configuration, access);

        public void Run(ExplorerRun run, int variant)
        {
            var dataReader = variant % 2 == 1;
            var late = LateOps[variant / 2 % 3];
            var closer = variant / 6 % 2 == 0 ? run.A : run.B;
            var overlap = variant / 12 == 1;
            var kind = dataReader ? "reader" : "enumerator";
            var db = run.Open();
            var tx = HandleChecks.Begin(run.A, db, run);
            var rows = tx.GetCollection("rows");
            run.A.Run("Insert.prior", () => rows.Insert(ExplorerModel.Row(4, 40)));

            IEnumerator<BsonDocument> cursor = null;
            IBsonDataReader reader = null;
            var open = run.A.Invoke("ReaderOpen", () =>
            {
                if (dataReader)
                {
                    reader = rows.Query().ExecuteReader();
                    ExplorerModel.Require(reader.Read(), "handle-reader-empty", "the bound data reader returned nothing");
                }
                else
                {
                    cursor = rows.FindAll().GetEnumerator();
                    ExplorerModel.Require(cursor.MoveNext(), "handle-reader-empty", "the bound enumerator returned nothing");
                }
            });
            run.A.Complete(open);
            run.Judge(open, Permit.Success);
            Action dispose = () => { if (dataReader) reader.Dispose(); else cursor.Dispose(); };

            if (overlap)
            {
                var inside = run.Schedule.NewBoundary("A inside a bound input callback");
                var writing = run.A.Invoke("Insert.callback", () => rows.Insert(ExplorerRun.Input(inside, () => { }, false)),
                    ExplorerSchedule.Extended);
                inside.Wait();
                var refused = run.C.Invoke("ReaderDispose.overlap", dispose);
                run.C.Complete(refused);
                HandleChecks.Refused(run, refused);
                HandleChecks.Active(tx, kind + " Dispose overlapping a bound call");
                inside.Let();
                run.A.Complete(writing);
                run.Judge(writing, Permit.Success);
            }

            var close = closer.Invoke("ReaderDispose", dispose);
            closer.Complete(close);
            run.Judge(close, Permit.Success);

            var use = run.C.Invoke("Reader.after-dispose." + late, () =>
            {
                switch (late)
                {
                    case "advance":
                        if (dataReader) reader.Read(); else cursor.MoveNext();
                        break;
                    case "current":
                        GC.KeepAlive(dataReader ? reader.Current : cursor.Current);
                        break;
                    default:
                        dispose();
                        break;
                }
            });
            run.C.Complete(use);
            // The transition first: whatever the late call observed, it must not have ended the transaction.
            HandleChecks.Active(tx, "late " + late + " on a disposed bound " + kind);
            run.Judge(use, Permit.Success | Permit.Disposed | Permit.Refusal);

            var commit = run.B.Invoke("Commit.after-disposed-reader", tx.Commit);
            run.B.Complete(commit);
            run.Judge(commit, Permit.Success);
            if (tx.State != LiteTransactionState.Committed)
                throw new ExplorerFailure("EXPLORER_HANDLE_NOT_COMMITTED", "Commit returned but the handle is " + tx.State);
            run.Model.Acknowledge("rows", 4, 40);
            if (overlap)
            {
                run.Model.Acknowledge("rows", 2, 20);
                run.Model.Acknowledge("rows", 3, 30);
            }
        }
    }
}
