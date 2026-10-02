using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A queued rebuild makes progress under a sustained stream of handles that hop threads and
    /// run ordinary reads inside their callbacks. Writer priority holds back every handle begun
    /// after the rebuild queued; only callbacks of an executing handle, which the rebuild waits
    /// for anyway, may read ahead of it. Adapted from #133's
    /// <c>TransactionHandleMaintenanceProgress_Tests</c>: the admission gate's
    /// <c>_waitingWriters</c> replaces <c>OperationLifetime</c>.
    /// </summary>
    public class TransactionHandleMaintenanceProgress_Tests
    {
        private const int Workers = 4;
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        private static object Field(object owner, string name) => owner.GetType().GetField(name, Fields).GetValue(owner);

        // The admission gate of the engine's current lock service. Rebuild replaces it on reopen.
        private static object Gate(LiteEngine engine) => Field(Field(engine, "_locker"), "_transaction");

        private static int WaitingWriters(object gate) => (int)Field(gate, "_waitingWriters");

        [Fact]
        public void Maintenance_progresses_under_sustained_handle_callback_reads()
        {
            using var file = new TempFile();
            var committed = Enumerable.Range(0, Workers).Select(_ => new List<int>()).ToArray();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 10 });
                db.GetCollection("rows").EnsureIndex("value");
                db.Timeout = TimeSpan.FromSeconds(30);

                object queuedGate = null;
                var errors = new ConcurrentQueue<Exception>();
                var overtook = new ConcurrentQueue<string>();
                var lateAttempts = 0;
                var heldBack = 0;
                var callbackReads = 0;
                var afterRebuild = new int[Workers];
                using var started = new CountdownEvent(Workers);
                using var stop = new ManualResetEventSlim();
                Task rebuild = null;

                // One handle iteration: begin here, insert from another thread with an input
                // callback that runs an ordinary read, then commit from a third thread.
                void Iterate(int worker, int sequence)
                {
                    var late = Volatile.Read(ref queuedGate) != null;
                    if (late) Interlocked.Increment(ref lateAttempts);
                    ILiteTransaction tx;
                    try { tx = db.BeginTransaction(); }
                    catch (LiteException ex) when (rebuild != null && ex.ErrorCode == LiteException.ENGINE_DISPOSED)
                    {
                        // Held behind the rebuild until it retired the admission gate.
                        Interlocked.Increment(ref heldBack);
                        Assert.True(SpinWait.SpinUntil(() => rebuild.IsCompleted, TimeSpan.FromSeconds(30)));
                        return;
                    }
                    // A handle begun after the rebuild queued must not hold a lease of the gate
                    // the rebuild is still waiting for.
                    if (late && ReferenceEquals(Gate(engine), queuedGate))
                        overtook.Enqueue($"worker {worker} sequence {sequence} (waiting writers {WaitingWriters(queuedGate)})");
                    using (tx)
                    {
                        IEnumerable<BsonDocument> Input()
                        {
                            // An ordinary read inside the executing handle's callback.
                            Assert.NotNull(db.GetCollection("rows").FindById(1));
                            Interlocked.Increment(ref callbackReads);
                            yield return new BsonDocument { ["_id"] = sequence };
                        }
                        Task.Run(() => tx.GetCollection("w" + worker).Insert(Input())).GetAwaiter().GetResult();
                        Task.Run(() => tx.Commit()).GetAwaiter().GetResult();
                    }
                    committed[worker].Add(sequence);
                    if (rebuild != null && rebuild.IsCompleted) afterRebuild[worker]++;
                }

                var threads = Enumerable.Range(0, Workers).Select(worker => new Thread(() =>
                {
                    try
                    {
                        var sequence = 1;
                        Iterate(worker, sequence++);
                        started.Signal();
                        while (!stop.IsSet) Iterate(worker, sequence++);
                    }
                    catch (Exception ex) { errors.Enqueue(ex); }
                }) { IsBackground = true }).ToArray();
                foreach (var thread in threads) thread.Start();

                // An anchor handle keeps a lease from another thread so the rebuild must queue.
                ILiteTransaction anchor = null;
                try
                {
                    Assert.True(started.Wait(TimeSpan.FromSeconds(30)), "Workers did not start.");
                    anchor = Task.Run(() => db.BeginTransaction()).Result;
                    var gate = Gate(engine);
                    rebuild = Task.Run(() => db.Rebuild());
                    Assert.True(SpinWait.SpinUntil(() => WaitingWriters(gate) == 1, TimeSpan.FromSeconds(10)),
                        "Rebuild did not queue behind the anchor handle.");
                    Volatile.Write(ref queuedGate, gate);
                    // The executing anchor's callback reads ahead of the queued writer (the
                    // writer waits for this handle anyway).
                    IEnumerable<BsonDocument> AnchorInput()
                    {
                        Assert.NotNull(db.GetCollection("rows").FindById(1));
                        yield return new BsonDocument { ["_id"] = 1 };
                    }
                    Assert.True(Task.Run(() => anchor.GetCollection("anchor").Insert(AnchorInput())).Wait(TimeSpan.FromSeconds(10)),
                        "A callback of the executing handle was queued behind the writer it blocks.");
                    // Barrier: every worker has started a begin after the rebuild queued.
                    Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref lateAttempts) >= Workers || !errors.IsEmpty,
                        TimeSpan.FromSeconds(30)), "Workers did not reach a begin after the rebuild queued.");
                    Assert.False(rebuild.IsCompleted);
                    Task.Run(() => anchor.Commit()).Wait();
                    Assert.True(rebuild.Wait(TimeSpan.FromSeconds(10)), "Rebuild starved under sustained handle reads.");
                    rebuild.GetAwaiter().GetResult();
                    // New work proceeds after maintenance.
                    Assert.True(SpinWait.SpinUntil(() => afterRebuild.All(n => n > 0) || !errors.IsEmpty, TimeSpan.FromSeconds(30)),
                        "Workers made no progress after the rebuild.");
                }
                finally
                {
                    stop.Set();
                    foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
                    anchor?.Dispose();
                }
                Assert.Empty(errors);
                Assert.Empty(overtook);
                Assert.True(Volatile.Read(ref callbackReads) > 0);
                Assert.True(Volatile.Read(ref heldBack) > 0, "No late begin was held behind the rebuild.");
                Assert.Equal(1, db.GetCollection("rows").Count(Query.EQ("value", 10)));
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var cold = new LiteDatabase(file);
                Assert.Equal(1, cold.GetCollection("rows").Count(Query.EQ("value", 10)));
                Assert.Equal(1, cold.GetCollection("anchor").Count());
                for (var worker = 0; worker < Workers; worker++)
                    Assert.Equal(committed[worker],
                        cold.GetCollection("w" + worker).FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
            }
        }
    }
}
