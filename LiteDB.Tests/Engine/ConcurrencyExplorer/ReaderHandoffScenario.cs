using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LiteDB.Utils;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// A reader crosses threads by sequential handoff: A opens it (inside its unit when the access
    /// kind is transactional and not thread-affine) and reads the first document, B advances it (its
    /// ReadTransform runs the callback dimension on B), and after B returned, E or A closes it, while
    /// the maintenance contender and a write run in one of six orders; then A completes its unit.
    /// Thread-affine kinds (legacy) are not applicable: their reader belongs to the beginning thread.
    /// Permitted: what the configuration races (see <see cref="CallbackPauseScenario.Disturbance"/>);
    /// a rebuild that needs exclusivity while the reader is open may give up its bounded wait. A
    /// reader that returns documents returns its snapshot: every document it yields has the value
    /// acknowledged when it opened, and a reader that ran to the end yielded exactly that snapshot.
    /// </summary>
    internal sealed class ReaderHandoffScenario : IExplorerScenario
    {
        private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(150);

        public string Name => "reader-handoff";
        public string Description => "A reader opened on A, advanced on B, closed on E or A, under maintenance and writes.";
        public int Variants => 6 * 2 * 2;

        public string NotApplicable(ExplorerConfiguration configuration, IExplorerAccess access) =>
            access != null && access.ThreadAffine
                ? "reader-handoff moves a reader across threads; a thread-affine unit (legacy BeginTrans) must run its " +
                  "operations on the thread that began it (docs/explicit-transactions.md), so the handoff is outside its contract"
                : null;

        public void Run(ExplorerRun run, int variant)
        {
            var order = ExplorerRun.Permutations[variant % 6];
            var closeOnOther = variant / 6 % 2 == 0;
            var dataReader = variant / 12 == 1;
            var c = run.Configuration;
            var disturbance = CallbackPauseScenario.Disturbance(c);
            var record = new CallbackRecord();
            Action callback = () => { };
            var armed = 1;
            var db = run.Open((collection, value) =>
            {
                if (collection == "rows" && Thread.CurrentThread.Name == "concurrency-explorer-B" && Interlocked.Exchange(ref armed, 0) == 1)
                    callback();
                return value;
            });
            var peer = c.Callback == ExplorerCallback.Peer ? run.Open() : null;
            var other = c.Callback == ExplorerCallback.OtherFile ? run.Open(other: true) : null;
            callback = run.Callback(db, peer, other, record);

            // More than 64 KiB of results, so a Shared reader streams under a lease instead of finishing under the mutex.
            var seed = run.A.Invoke("InsertMany", () =>
                db.GetCollection("rows").Insert(Enumerable.Range(100, 12).Select(id => ExplorerModel.Row(id, id))));
            run.A.Complete(seed);
            run.Judge(seed, Permit.Success);
            foreach (var id in Enumerable.Range(100, 12)) run.Model.Acknowledge("rows", id, id);
            var snapshot = Enumerable.Range(100, 12).Select(id => (id, value: id)).Concat(new[] { (id: 1, value: 10) })
                .ToDictionary(pair => pair.id, pair => pair.value);

            IExplorerUnit unit = null;
            IEnumerator<BsonDocument> cursor = null;
            IBsonDataReader reader = null;
            var seen = new List<BsonDocument>();
            var open = run.A.Invoke("ReaderOpen", () =>
            {
                unit = run.Access.Begin(db);
                if (dataReader)
                {
                    reader = db.Execute("SELECT $ FROM rows");
                    if (reader.Read()) seen.Add(reader.Current.AsDocument);
                }
                else
                {
                    cursor = unit.Collection("rows").FindAll().GetEnumerator();
                    if (cursor.MoveNext()) seen.Add(cursor.Current);
                }
            });
            run.A.Complete(open);
            run.Judge(open, Permit.Success);
            var finished = false;
            var advance = new Contender
            {
                Op = "ReaderAdvance",
                Body = () =>
                {
                    for (var i = 0; i < 3; i++)
                    {
                        var more = dataReader ? reader.Read() : cursor.MoveNext();
                        if (!more) { finished = true; break; }
                        seen.Add(dataReader ? reader.Current.AsDocument : cursor.Current);
                    }
                },
                Account = work =>
                {
                    Reachability.Sometimes("situation:explorer-reader-advanced-on-another-thread");
                    // The close below is sequential, so the advance only races the configuration's disturbance.
                    run.Judge(work, disturbance);
                }
            };
            var contenders = ExplorerContenders.For(run, db, slot: 1);
            contenders[0] = advance;
            var actors = new[] { run.B, run.C, run.D };
            var works = new List<(Contender Contender, ExplorerSchedule.Work Work, ExplorerSchedule.Actor Actor)>();
            // The advance always runs on B, so its ReadTransform callback runs on B.
            foreach (var index in order)
            {
                var actor = index == 0 ? run.B : actors.First(item => item != run.B && works.All(w => w.Actor != item));
                var work = actor.Invoke(contenders[index].Op, () => contenders[index].Body(), contenders[index].Deadline);
                works.Add((contenders[index], work, actor));
                run.Settle(work, SettleTime);
            }
            // Sequential handoff: the reader is closed only after B's advance returned (a reader is not safe
            // for concurrent use; overlapping calls on one reader are outside every contract).
            var advanced = works.First(w => w.Actor == run.B).Work;
            run.B.Complete(advanced);
            var closer = closeOnOther ? run.E : run.A;
            var close = closer.Invoke("ReaderClose", () =>
            {
                if (dataReader) reader.Dispose();
                else cursor.Dispose();
            });
            closer.Complete(close);
            run.Judge(close, disturbance);
            var commit = unit.Transactional && run.Vector.Seed % 2 == 0;
            var committed = false;
            var finish = run.A.Invoke(commit ? "Commit" : "Rollback", () =>
            {
                if (commit) committed = unit.Commit();
                else unit.Rollback();
            });
            run.A.Complete(finish);
            run.Judge(finish, disturbance);
            foreach (var item in works) item.Actor.Complete(item.Work);
            foreach (var item in works) item.Contender.Account(item.Work);
            run.JudgeCallback(record, "callback on B", disturbance);
            // The callback's nested write is ordinary work on its connection, not part of A's unit.
            run.Account(record, outerAcknowledged: true);
            foreach (var row in seen)
                ExplorerModel.Require(snapshot.TryGetValue(row["_id"].AsInt32, out var value) && row["value"].AsInt32 == value,
                    "reader-snapshot", "the reader yielded " + row["_id"] + "=" + row["value"] + ", not its snapshot's value");
            if (finished && advance.Account != null)
                ExplorerModel.Require(seen.Select(row => row["_id"].AsInt32).OrderBy(id => id).SequenceEqual(snapshot.Keys.OrderBy(id => id)),
                    "reader-snapshot-complete", "a reader that ran to its end missed or added documents");
            if (c.Callback == ExplorerCallback.Dispose && record.Ran && record.Error == null) run.Disposed(db, "Dispose (from callback)");
            if (run.IsDisposed(db)) CallbackPauseScenario.LateOperationRefused(run, db);
        }
    }
}
