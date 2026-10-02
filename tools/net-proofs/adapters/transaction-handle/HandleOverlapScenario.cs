using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Handle alphabet (fork PR #133 explorer, schedules 0-11, generalized): A's handle is executing
    /// an insert paused inside its bound input callback; B then tries, in one of six orders, a bound
    /// read, Commit and Rollback on the same handle. Each must be refused before executing (never a
    /// timeout) and leave the handle Active. Released, A's insert completes; A opens a bound reader;
    /// Commit on B is refused while the reader is open; C advances the transferred reader to its end
    /// and disposes it (sequential handoff); B commits or rolls back by the seed bit (completion on
    /// another thread); a later use of the disposed reader is refused (disposed or completed handle).
    /// Inside the callback, ordinary work on another connection to the same file is refused in
    /// Shared mode (its writer ownership is held by the executing handle) and succeeds in Direct
    /// mode for another collection; work on another file always succeeds (positive control).
    /// </summary>
    internal sealed class HandleOverlapScenario : IExplorerScenario
    {
        public string Name => "handle-overlap";
        public string Description => "Overlapping calls on one executing handle, commit with an open reader, reader and completion handoff.";
        public int Variants => 6 * 2;

        public string NotApplicable(ExplorerConfiguration configuration, IExplorerAccess access) => HandleChecks.OnlyHandle(configuration, access);

        public void Run(ExplorerRun run, int variant)
        {
            var order = ExplorerRun.Permutations[variant % 6];
            var c = run.Configuration;
            var db = run.Open();
            var peer = variant / 6 == 1 ? run.Open() : db;
            var other = run.Open(other: true);
            var tx = HandleChecks.Begin(run.A, db, run);
            var rows = tx.GetCollection("rows");
            var inside = run.Schedule.NewBoundary("A inside its bound input callback");
            Exception ordinaryError = null;
            var writing = run.A.Invoke("Insert.callback", () => rows.Insert(ExplorerRun.Input(inside, () =>
            {
                try { peer.GetCollection("other").Insert(ExplorerModel.Row(2, 50)); }
                catch (Exception error) { ordinaryError = error; }
                other.GetCollection("rows").Insert(ExplorerModel.Row(2, 60));
            }, false)), ExplorerSchedule.Extended);
            inside.Wait();
            var contenders = new (string Op, Action Body)[]
            {
                ("Read.overlap", () => rows.FindById(1)),
                ("Commit.overlap", tx.Commit),
                ("Rollback.overlap", tx.Rollback)
            };
            foreach (var index in order)
            {
                var work = run.B.Invoke(contenders[index].Op, contenders[index].Body);
                run.B.Complete(work);
                HandleChecks.Refused(run, work);
                HandleChecks.Active(tx, contenders[index].Op);
            }
            inside.Let();
            run.A.Complete(writing);
            run.Judge(writing, Permit.Success);
            run.Other.Acknowledge("rows", 2, 60);
            if (c.Shared)
            {
                if (ordinaryError == null)
                    throw new ExplorerFailure("EXPLORER_HANDLE_ORDINARY_ADMITTED", "Shared ordinary write on the same file succeeded inside a bound callback");
                ExplorerJudge.Judge("A callback", "Callback", ordinaryError, Permit.Refusal | Permit.LockTimeout);
            }
            else
            {
                ExplorerJudge.Judge("A callback", "Callback", ordinaryError, Permit.Success);
                run.Model.Acknowledge("other", 2, 50);
            }

            IEnumerator<BsonDocument> cursor = null;
            var open = run.A.Invoke("ReaderOpen", () =>
            {
                cursor = rows.FindAll().GetEnumerator();
                ExplorerModel.Require(cursor.MoveNext(), "handle-reader-empty", "the bound reader returned nothing");
            });
            run.A.Complete(open);
            run.Judge(open, Permit.Success);
            var refused = run.B.Invoke("Commit.with-reader", tx.Commit);
            run.B.Complete(refused);
            HandleChecks.Refused(run, refused);
            HandleChecks.Active(tx, "Commit.with-reader");
            var ids = new List<int>();
            var transfer = run.C.Invoke("ReaderTransfer", () =>
            {
                ids.Add(cursor.Current["_id"].AsInt32);
                while (cursor.MoveNext()) ids.Add(cursor.Current["_id"].AsInt32);
                cursor.Dispose();
            });
            run.C.Complete(transfer);
            run.Judge(transfer, Permit.Success);
            ExplorerModel.Require(ids.OrderBy(id => id).SequenceEqual(new[] { 1, 2, 3 }), "handle-reader-contents",
                "the transferred bound reader returned " + string.Join(",", ids));
            var commit = run.Vector.Seed % 2 == 0;
            var finish = run.B.Invoke(commit ? "Commit.handoff" : "Rollback.handoff", commit ? (Action)tx.Commit : tx.Rollback);
            run.B.Complete(finish);
            run.Judge(finish, Permit.Success);
            if (commit)
            {
                run.Model.Acknowledge("rows", 2, 20);
                run.Model.Acknowledge("rows", 3, 30);
            }
            var late = run.C.Invoke("Reader.after-dispose", () => cursor.MoveNext());
            run.C.Complete(late);
            // Later fork revisions document ObjectDisposedException precedence; the invariant is that nothing is admitted.
            if (late.Ok)
                throw new ExplorerFailure("EXPLORER_HANDLE_DISPOSED_READER_ADMITTED", "a disposed bound reader was advanced after its handle completed");
            run.Judge(late, Permit.Disposed | Permit.Refusal);
        }
    }
}
