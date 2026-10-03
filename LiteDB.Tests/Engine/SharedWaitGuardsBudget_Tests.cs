using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Tests.Engine.SharedWaitGuards_Tests;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A SharedWriterTimeout budget is spent only on waiting for another owner: this connection's
    /// own release in flight is not charged to it, and a timeout names the owner it waited behind.
    /// </summary>
    public class SharedWaitGuardsBudget_Tests
    {
        public enum WritePath { IteratorInput, ReadTransform }

        [Theory]
        [InlineData(0, WritePath.IteratorInput)] [InlineData(0, WritePath.ReadTransform)]
        [InlineData(1, WritePath.IteratorInput)] [InlineData(1, WritePath.ReadTransform)]
        public void Zero_and_tiny_budgets_never_time_out_on_an_uncontended_connection(int timeoutMilliseconds, WritePath path)
        {
            using var file = new TempFile();
            Seed(file);
            var settings = Settings(file, timeout: TimeSpan.FromMilliseconds(timeoutMilliseconds));
            // Both make a write acquire through the connection's holder thread, whose release
            // of the previous write is still in flight when the next write begins.
            if (path == WritePath.ReadTransform) settings.ReadTransform = (_, value) => value;
            // 200 keeps the test within the suite's time budget; before the fix a zero budget failed
            // well over half of such writes, and a 1 ms budget a few to a quarter of them.
            const int writes = 200;
            var failures = new List<string>();
            using (var engine = new SharedEngine(settings))
            using (var db = new LiteDatabase(engine))
            {
                var rows = db.GetCollection("rows");
                for (var i = 0; i < writes; i++)
                {
                    var error = Record.Exception(() =>
                    {
                        if (path == WritePath.IteratorInput) rows.Insert(Row(100 + i));
                        else engine.Insert("rows", new[] { Row(100 + i) }, BsonAutoId.Int32);
                    });
                    if (error != null) failures.Add(error.Message);
                }
                Assert.True(failures.Count == 0, $"{failures.Count} of {writes} writes failed; first: {failures.FirstOrDefault()}");
                Assert.Equal(0, db.GetSharedWaitDiagnostics().Total.TimedOut);
            }
            Verify(file, null, new[] { 1 }.Concat(Enumerable.Range(100, writes)).ToArray());
        }

        [Theory]
        [InlineData(WritePath.IteratorInput)]
        [InlineData(WritePath.ReadTransform)]
        public void Parallel_writers_on_one_connection_blame_the_right_owner(WritePath path)
        {
            using var file = new TempFile();
            Seed(file);
            var settings = Settings(file, timeout: TimeSpan.Zero);
            if (path == WritePath.ReadTransform) settings.ReadTransform = (_, value) => value;
            const int threads = 4, writes = 100;
            var timeouts = 0;
            var wrong = new List<string>();
            using (var engine = new SharedEngine(settings))
            using (var db = new LiteDatabase(engine))
            {
                using var start = new Barrier(threads);
                var workers = Enumerable.Range(0, threads).Select(t => Unmarked(() =>
                {
                    start.SignalAndWait();
                    for (var i = 0; i < writes; i++)
                    {
                        try
                        {
                            if (path == WritePath.IteratorInput) db.GetCollection("rows").Insert(Row(1000 * (t + 1) + i));
                            else engine.Insert("rows", new[] { Row(1000 * (t + 1) + i) }, BsonAutoId.Int32);
                        }
                        catch (LiteException error) when (error.ErrorCode == LiteException.LOCK_TIMEOUT)
                        {
                            Interlocked.Increment(ref timeouts);
                            // No other connection or process exists: every owner is a thread of this connection.
                            if (!error.Message.Contains("this connection")) lock (wrong) wrong.Add(error.Message);
                        }
                    }
                })).ToArray();
                Assert.True(Task.WaitAll(workers, TimeSpan.FromSeconds(120)), "The parallel writers did not finish.");
                Assert.True(wrong.Count == 0, $"{wrong.Count} of {timeouts} timeouts blamed another owner; first: {wrong.FirstOrDefault()}");
                // The contention was real: with a zero budget some writes queued behind another thread.
                Assert.True(timeouts > 0, "No write timed out; the test exercised nothing.");
                var diagnostics = db.GetSharedWaitDiagnostics();
                Assert.Equal(timeouts, diagnostics.Total.TimedOut);
                Assert.Equal(0, diagnostics.CurrentWaiters);
            }
        }

        // The process's local queue of transaction handles for the engine's database.
        private static SemaphoreSlim HandleQueue(SharedEngine engine)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var name = (string)typeof(SharedEngine).GetField("_mutexName", flags).GetValue(engine);
            var queues = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(SharedEngine).GetField("TransactionWriters", flags).GetValue(null);
            return queues.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        }

        [Fact]
        public void Timeout_behind_a_handle_being_admitted_names_that_handle()
        {
            using var file = new TempFile();
            Seed(file);
            using (var db = new LiteDatabase(new SharedEngine(Settings(file, timeout: TimeSpan.FromMilliseconds(200)))))
            using (var unboundedEngine = new SharedEngine(Settings(file)))
            using (var unbounded = new LiteDatabase(unboundedEngine))
            using (var other = new LiteDatabase(new SharedEngine(Settings(file))))
            {
                using var held = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var legacy = Unmarked(() =>
                {
#pragma warning disable CS0618
                    other.BeginTrans();
                    other.GetCollection("rows").Insert(Row(2));
                    held.Set();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(20)));
                    other.Commit();
#pragma warning restore CS0618
                });
                Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
                // This handle takes the process's local handle queue, then waits for native admission.
                var admitting = Unmarked(() =>
                {
                    using var tx = unbounded.BeginTransaction();
                    tx.GetCollection("rows").Insert(Row(3));
                    tx.Commit();
                });
                var queue = HandleQueue(unboundedEngine);
                Assert.True(SpinWait.SpinUntil(() => queue.CurrentCount == 0 && unbounded.GetSharedWaitDiagnostics().CurrentWaiters == 1,
                    TimeSpan.FromSeconds(10)), "The handle never queued for native admission.");
                var timeout = Assert.Throws<LiteException>(() => db.BeginTransaction());
                Assert.Equal(LiteException.LOCK_TIMEOUT, timeout.ErrorCode);
                Assert.Contains("this process's transaction handle (admitting)", timeout.Message);
                release.Set();
                Assert.True(legacy.Wait(TimeSpan.FromSeconds(20)));
                Assert.True(admitting.Wait(TimeSpan.FromSeconds(20)));
            }
            Verify(file, null, 1, 2, 3);
        }
    }
}
