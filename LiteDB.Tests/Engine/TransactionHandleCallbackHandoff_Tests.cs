using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using LiteDB.Engine;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleCallbackHandoff_Tests
    {
        // A failed, unjoinable worker must not race fixture deletion or engine close.
        // Retain its graph for diagnostics until this test host exits.
        private static readonly ConcurrentBag<object> BlockedFixtures = new ConcurrentBag<object>();
        private readonly ITestOutputHelper _output;
        public TransactionHandleCallbackHandoff_Tests(ITestOutputHelper output) { _output = output; }
        private const string Overlap = "Overlapping or reentrant transaction handle use is not supported.";

        [Theory]
        [InlineData(false, null)]
        [InlineData(false, "secret")]
        [InlineData(true, null)]
        [InlineData(true, "secret")]
        public void Public_admission_handoff_preserves_current_callback_marker(bool shared, string password)
        {
            var file = new TempFile();
            var settings = new ConnectionString { Filename = file, Password = password,
                Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };
            LiteDatabase db = null;
            ILiteTransaction tx = null;
            var workers = new List<Thread>();
            try
            {
                db = new LiteDatabase(settings);
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 10 });
                db.GetCollection("rows").EnsureIndex("value");
                db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
                tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = 20 });
                var handle = (LiteTransaction)tx;
                var context = (TransactionContext)typeof(LiteTransaction).GetField("_transaction",
                    BindingFlags.NonPublic | BindingFlags.Instance).GetValue(tx);
                var errors = new ConcurrentQueue<string>();
                var clock = Stopwatch.StartNew();
                VerifyReleaseOrder(handle, context, workers, errors);
                Assert.Empty(errors);
                _output.WriteLine(RunContenders(handle, context, workers, errors, clock));
                foreach (var error in errors) _output.WriteLine(error);
                Assert.Empty(errors);
                Assert.Null(context.ExecutingThread);
                Assert.Equal(LiteTransactionState.Active, tx.State);
                tx.Commit();
                tx.Dispose();
                tx = null;
                db.Dispose();
                db = null;
                for (var reopen = 0; reopen < 2; reopen++)
                {
                    using var cold = new LiteDatabase(settings);
                    var query = cold.GetCollection("rows").Query().Where(Query.GTE("value", 10));
                    Assert.Equal("value", query.GetPlan()["index"]["name"].AsString);
                    Assert.Equal(new[] { 1, 2 }, query.ToArray().Select(row => row["_id"].AsInt32).OrderBy(id => id));
                    Assert.Equal(2, cold.GetCollection("rows").Count());
                    Assert.NotNull(cold.GetCollection("sentinel").FindById(9));
                }
            }
            catch (Exception error)
            {
                // Inspect after helper finally blocks released barriers and joined.
                // An exception filter would run before that cleanup.
                if (workers.Any(worker => worker.IsAlive))
                    throw new InvalidOperationException("Handoff worker did not drain; live fixture retained at " + file.Filename, error);
                throw;
            }
            finally
            {
                var safeToClose = workers.All(worker => !worker.IsAlive);
                if (!safeToClose)
                    BlockedFixtures.Add(new object[] { file, db, tx, workers });
                else
                {
                    try { tx?.Dispose(); }
                    finally { try { db?.Dispose(); } finally { file.Dispose(); } }
                }
            }
        }

        private static void VerifyReleaseOrder(LiteTransaction handle, TransactionContext context,
            List<Thread> workers, ConcurrentQueue<string> errors)
        {
            var entered = new ManualResetEventSlim();
            var returnAllowed = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                try
                {
                    handle.Run(() => { entered.Set(); returnAllowed.Wait(); return true; });
                }
                catch (Exception error) { errors.Enqueue("forced release: " + error); }
            }) { IsBackground = true };
            workers.Add(worker);
            var gate = typeof(LiteTransaction).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(handle);
            var locked = false;
            worker.Start();
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(15)), "Forced callback did not start.");
                Assert.True(Monitor.TryEnter(gate, TimeSpan.FromSeconds(15)), "Could not hold the admission gate.");
                locked = true;
                returnAllowed.Set();
                // Exit cannot release admission while this test owns _gate. Scope
                // restoration must therefore finish before Exit, not after it.
                Assert.True(SpinWait.SpinUntil(() => context.ExecutingThread == null, TimeSpan.FromSeconds(15)),
                    "Callback marker was not restored before releasing public admission.");
            }
            finally
            {
                returnAllowed.Set();
                if (locked) Monitor.Exit(gate);
                if (worker.Join(TimeSpan.FromSeconds(5))) { entered.Dispose(); returnAllowed.Dispose(); }
                else errors.Enqueue("Forced release worker did not drain after its barrier was released.");
            }
        }

        private static string RunContenders(LiteTransaction handle, TransactionContext context,
            List<Thread> workers, ConcurrentQueue<string> errors, Stopwatch clock)
        {
            var start = new ManualResetEventSlim();
            var progress = new TransactionHandoffProgress(4, 1000, clock.ElapsedMilliseconds);
            var stop = 0;
            try
            {
                for (var workerIndex = 0; workerIndex < 4; workerIndex++)
                {
                    var index = workerIndex;
                    var worker = new Thread(() =>
                    {
                        start.Wait();
                        for (var completed = 0; completed < 1000 && Volatile.Read(ref stop) == 0;)
                        {
                            var callbackEntered = false;
                            progress.Attempt(index);
                            try
                            {
                                handle.Run(() =>
                                {
                                    callbackEntered = true;
                                    if (!ReferenceEquals(context.ExecutingThread, Thread.CurrentThread)) errors.Enqueue($"worker {index}: before");
                                    Thread.Yield();
                                    if (!ReferenceEquals(context.ExecutingThread, Thread.CurrentThread)) errors.Enqueue($"worker {index}: after");
                                    return true;
                                });
                                completed++;
                                progress.Succeeded(index, clock.ElapsedMilliseconds);
                            }
                            catch (InvalidOperationException error) when (!callbackEntered && error.GetType() == typeof(InvalidOperationException) && error.Message == Overlap)
                            {
                                progress.Rejected(index);
                                Thread.Yield();
                            }
                            catch (Exception error)
                            {
                                errors.Enqueue($"worker {index}, callbackEntered={callbackEntered}: {error}");
                                return;
                            }
                        }
                    }) { IsBackground = true };
                    workers.Add(worker);
                    worker.Start();
                }
                start.Set();
                // This monitor remains outside Run, so a blocked operation cannot
                // prevent its own watchdog from noticing a lack of completed work.
                while (workers.Any(worker => worker.IsAlive) && errors.IsEmpty)
                {
                    var failure = progress.Timeout(clock.ElapsedMilliseconds);
                    if (failure != null) { errors.Enqueue(failure); break; }
                    Thread.Sleep(1);
                }
                if (clock.ElapsedMilliseconds >= 60000) errors.Enqueue(progress.Timeout(clock.ElapsedMilliseconds));
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                start.Set();
                var drain = Stopwatch.StartNew();
                foreach (var worker in workers)
                {
                    var remaining = TimeSpan.FromSeconds(5) - drain.Elapsed;
                    if (worker.IsAlive && (remaining <= TimeSpan.Zero || !worker.Join(remaining)))
                        errors.Enqueue("Worker still inside Run after stop; storage retained. " + progress.Describe(clock.ElapsedMilliseconds));
                }
                if (workers.All(worker => !worker.IsAlive)) start.Dispose();
            }
            var summary = progress.Describe(clock.ElapsedMilliseconds);
            if (!progress.AllCompleted) errors.Enqueue("Incomplete handoffs. " + summary);
            return summary;
        }
    }
}
