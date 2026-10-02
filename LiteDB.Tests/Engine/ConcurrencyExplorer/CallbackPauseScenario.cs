using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB.Tests.Safety;
using LiteDB.Utils;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Actor A runs an operation that calls user code (a lazy input sequence, a ReadTransform, an
    /// upload stream, or an input sequence's finally block that runs while the operation tears its
    /// input down) and is paused inside that callback at a forced boundary. Three contenders on B, C
    /// and D (a read, the maintenance of the configuration or a checkpoint, a write) start in one
    /// of six orders; A is released after the first contender or after all three; when released, A's
    /// callback does what the callback dimension says. Permitted outcomes follow from what races
    /// the operation (<see cref="Disturbance"/>), never from a known defect: without maintenance
    /// every operation succeeds; a close permits disposed/refused failures; a rebuild permits a
    /// clean failure of an operation queued behind it (tested by Issue2965) that a retry repairs; a
    /// fatal stop permits the fault and disposed failures. Always: every operation completes within
    /// its deadline, acknowledged effects survive a cold reopen exactly, a disposed connection
    /// admits no later work, and (Shared) the writer mutex protects every core until its teardown.
    /// </summary>
    internal sealed class CallbackPauseScenario : IExplorerScenario
    {
        private static readonly string[] Points = { "input", "read-transform", "upload", "input-teardown" };
        private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(150);
        internal const string FileId = "$/explorer";

        public string Name => "callback-pause";
        public string Description => "An operation paused inside its user callback while contenders run (maintenance, reads, writes).";
        public int Variants => Points.Length * 6 * 2;

        public string NotApplicable(ExplorerConfiguration configuration, IExplorerAccess access) => null;

        internal static Permit Disturbance(ExplorerConfiguration c)
        {
            var permit = Permit.Success;
            if (c.Maintenance == ExplorerMaintenance.Close || c.Callback == ExplorerCallback.Dispose) permit |= Permit.Disposed | Permit.Refusal;
            if (c.Maintenance == ExplorerMaintenance.Rebuild) permit |= Permit.Disposed;
            if (c.Maintenance == ExplorerMaintenance.Fatal) permit |= Permit.Fatal | Permit.Disposed;
            return permit;
        }

        public void Run(ExplorerRun run, int variant)
        {
            var point = Points[variant % Points.Length];
            var order = ExplorerRun.Permutations[variant / Points.Length % 6];
            var releaseEarly = variant / (Points.Length * 6) == 1;
            var c = run.Configuration;
            var disturbance = Disturbance(c);
            var pause = run.Schedule.NewBoundary("A paused in its " + point + " callback");
            var record = new CallbackRecord();
            Action callback = () => { };
            var armed = 1;
            Func<string, BsonValue, BsonValue> transform = null;
            if (point == "read-transform")
                transform = (collection, value) =>
                {
                    if (collection == "rows" && System.Threading.Thread.CurrentThread.Name == "concurrency-explorer-A" &&
                        System.Threading.Interlocked.Exchange(ref armed, 0) == 1)
                    {
                        pause.Hit();
                        callback();
                    }
                    return value;
                };
            var db = run.Open(transform);
            var peer = c.Callback == ExplorerCallback.Peer ? run.Open() : null;
            var other = c.Callback == ExplorerCallback.OtherFile ? run.Open(other: true) : null;
            callback = run.Callback(db, peer, other, record);
            if (c.Callback == ExplorerCallback.Peer) Reachability.Sometimes("situation:explorer-peer-call-from-callback");
            if (c.Callback == ExplorerCallback.SameConnection) Reachability.Sometimes("situation:explorer-same-connection-call-from-callback");

            IExplorerUnit unit = null;
            var begin = run.A.Invoke("Begin", () => unit = run.Access.Begin(db));
            run.A.Complete(begin);
            run.Judge(begin, Permit.Success);
            var content = Enumerable.Range(0, 300 * 1024).Select(i => (byte)(i * 7)).ToArray();
            var op = point == "read-transform" ? "Find.callback" : point == "upload" ? "Upload.callback" : "Insert.callback";
            List<BsonDocument> read = null;
            var active = run.A.Invoke(op, () =>
            {
                if (point == "read-transform") read = unit.Collection("rows").FindAll().ToList();
                else if (point == "upload") db.FileStorage.Upload(FileId, "explorer.bin", new PausingStream(content, pause, callback));
                else unit.Collection("rows").Insert(ExplorerRun.Input(pause, callback, point == "input-teardown"));
            }, ExplorerSchedule.Extended);
            run.Schedule.Until(() => pause.Reached || active.Done.IsSet, "A reaches its callback", "A/" + op);
            run.Schedule.Decision("A paused: " + pause.Reached);

            var contenders = this.Contenders(run, db);
            var works = new List<(Contender Contender, ExplorerSchedule.Work Work)>();
            var actors = new[] { run.B, run.C, run.D };
            for (var i = 0; i < 3; i++)
            {
                var contender = contenders[order[i]];
                var work = actors[i].Invoke(contender.Op, () => contender.Body(), contender.Deadline);
                works.Add((contender, work));
                run.Settle(work, SettleTime);
                if (i == 0 && releaseEarly) pause.Let();
            }
            pause.Let();
            run.A.Complete(active);
            run.Judge(active, disturbance);
            if (read != null)
                ExplorerModel.Require(read.Any(row => row["_id"] == 1 && row["value"] == 10), "read-transform-result", "row 1 missing from A's read");
            run.JudgeCallback(record, "A callback", disturbance);

            // A's unit completes before the contenders are awaited: a contender may legitimately wait for it
            // (a Shared connection's other threads wait for its explicit transaction; Direct writers for its locks).
            var outcome = active.Ok ? Outcome.Committed : Outcome.Uncertain;
            if (unit != null && unit.Transactional)
            {
                var commit = active.Ok && run.Vector.Seed % 2 == 0;
                var committed = false;
                var complete = run.A.Invoke(commit ? "Commit" : "Rollback", () =>
                {
                    if (commit) committed = unit.Commit();
                    else unit.Rollback();
                });
                run.A.Complete(complete);
                run.Judge(complete, disturbance);
                outcome = !commit ? Outcome.Aborted : !complete.Ok ? Outcome.Uncertain : committed ? Outcome.Committed : Outcome.Aborted;
                if (commit && complete.Ok && !committed && disturbance == Permit.Success)
                    throw new ExplorerFailure("EXPLORER_COMMIT_LOST", "Commit reported no transaction although every operation succeeded");
            }
            for (var i = 0; i < works.Count; i++) actors[i].Complete(works[i].Work);
            foreach (var item in works) item.Contender.Account(item.Work);
            Account(run, point, content, outcome);
            var acknowledged = outcome == Outcome.Committed;
            run.Account(record, acknowledged);
            if (c.Callback == ExplorerCallback.Dispose && record.Ran && record.Error == null)
            {
                Reachability.Sometimes("situation:explorer-callback-disposed-its-own-connection");
                run.Disposed(db, "Dispose (from callback)");
            }
            if (run.IsDisposed(db)) LateOperationRefused(run, db);
        }

        internal enum Outcome { Committed, Aborted, Uncertain }

        private static void Account(ExplorerRun run, string point, byte[] content, Outcome outcome)
        {
            if (point == "read-transform" || outcome == Outcome.Aborted) return;
            if (point == "upload")
            {
                if (outcome == Outcome.Committed) run.Model.AcknowledgeFile(FileId, content);
                else run.Model.UncertainFile(FileId, content);
                return;
            }
            foreach (var row in new[] { (Id: 2, Value: 20), (Id: 3, Value: 30) })
            {
                if (outcome == Outcome.Committed) run.Model.Acknowledge("rows", row.Id, row.Value);
                else run.Model.Uncertain("rows", row.Id, row.Value);
            }
        }

        /// <summary>After Dispose returned, the connection admits no new work.</summary>
        internal static void LateOperationRefused(ExplorerRun run, LiteDatabase db)
        {
            var late = run.D.Invoke("Read.after-dispose", () => db.GetCollection("sentinel").FindById(42));
            run.D.Complete(late);
            if (late.Ok)
                throw new ExplorerFailure("EXPLORER_ADMITTED_AFTER_DISPOSE", "a read on a connection whose Dispose returned succeeded");
            Reachability.Sometimes("situation:explorer-operation-after-dispose-refused");
            run.Judge(late, Permit.Disposed | Permit.Refusal);
        }

        private List<Contender> Contenders(ExplorerRun run, LiteDatabase db) => ExplorerContenders.For(run, db, slot: 0);
    }
}
