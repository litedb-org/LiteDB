using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Tests.Engine.SharedWaitGuards_Tests;
using static LiteDB.Tests.Engine.SharedWaitGuardsMutexes;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A SharedWriterTimeout that runs out in one stage of the wait (the cross-process turnstile,
    /// the native mutex, or the process's local handle queue) leaves nothing of that wait owned.
    /// </summary>
    public class SharedWaitGuardsStages_Tests
    {
        public enum Stage { Turnstile, Main }

        public enum Path { Scoped, Holder, Begin }

        private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan Late = TimeSpan.FromMilliseconds(250);

        // A write or begin of the waiting connection: an array input acquires on the calling thread
        // (scoped), a collection insert through the connection's holder thread, a begin through the
        // handle's own holder thread.
        private static void Write(SharedEngine engine, LiteDatabase db, Path path, int id)
        {
            if (path == Path.Scoped) engine.Insert("rows", new[] { Row(id) }, BsonAutoId.Int32);
            else if (path == Path.Holder) db.GetCollection("rows").Insert(Row(id));
            else
            {
                using var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(id));
                tx.Commit();
            }
        }

        private static void AssertWithinBudget(TimeSpan elapsed) =>
            Assert.True(elapsed >= Budget - TimeSpan.FromMilliseconds(50) && elapsed <= Budget + Late, $"Timed out after {elapsed}");

        [Theory]
        [InlineData(Stage.Turnstile, Path.Scoped)] [InlineData(Stage.Turnstile, Path.Holder)] [InlineData(Stage.Turnstile, Path.Begin)]
        [InlineData(Stage.Main, Path.Scoped)] [InlineData(Stage.Main, Path.Holder)] [InlineData(Stage.Main, Path.Begin)]
        public void Turnstile_stage_timeout_leaves_no_ownership(Stage stage, Path path)
        {
            using var file = new TempFile();
            Seed(file);
            using (var engine = new SharedEngine(Settings(file, timeout: Budget)))
            using (var db = new LiteDatabase(engine))
            using (var other = new LiteDatabase(new SharedEngine(Settings(file))))
            {
                // Uncontended first, so the timed wait below measures the wait alone.
                Write(engine, db, path, 100);
                var turn = TurnMutex(engine);
                var main = MainMutex(engine);
                var before = db.GetSharedWaitDiagnostics().Total;
                // Another party (as a process would) holds the turnstile, so the wait times out
                // queuing for it; or holds the native mutex, so it times out while queued there.
                using (var holder = new Holder(stage == Stage.Turnstile ? turn : main))
                {
                    var elapsed = Stopwatch.StartNew();
                    var error = Record.Exception(() => Write(engine, db, path, 3));
                    elapsed.Stop();
                    var timeout = Assert.IsType<LiteException>(error);
                    Assert.Equal(LiteException.LOCK_TIMEOUT, timeout.ErrorCode);
                    Assert.Contains("another connection or process", timeout.Message);
                    AssertWithinBudget(elapsed.Elapsed);
                    // The gate this wait did not get stuck in is free for a third party right away:
                    // the native mutex was never taken, or the turnstile place was given up.
                    Assert.True(FreeOnAnotherThread(stage == Stage.Turnstile ? main : turn),
                        stage == Stage.Turnstile ? "The timed-out wait took the native mutex." : "The timed-out wait kept its turnstile place.");
                    var after = db.GetSharedWaitDiagnostics();
                    Assert.Equal(1, after.Total.TimedOut - before.TimedOut);
                    Assert.Equal(1, after.Total.Count - before.Count);
                    Assert.Equal(0, after.CurrentWaiters);
                    Assert.Equal(0, db.TransactionHandles.ActiveCount);
                    holder.Release();
                }
                Assert.True(FreeOnAnotherThread(turn), "The turnstile stayed owned.");
                Assert.True(FreeOnAnotherThread(main), "The native mutex stayed owned.");
                // Both connections write again.
                Write(engine, db, path, 4);
                other.GetCollection("rows").Insert(Row(5));
            }
            Verify(file, null, 1, 4, 5, 100);
        }

        [Fact]
        public void Local_queue_stage_timeout_leaves_no_ownership()
        {
            using var file = new TempFile();
            Seed(file);
            using (var db = new LiteDatabase(new SharedEngine(Settings(file, timeout: Budget))))
            using (var ownerEngine = new SharedEngine(Settings(file)))
            using (var owner = new LiteDatabase(ownerEngine))
            {
                Unmarked(() => { using var tx = db.BeginTransaction(); tx.Commit(); }).Wait();
                // A handle of another connection in this process holds the local handle queue.
                var first = Unmarked(() => { var tx = owner.BeginTransaction(); tx.GetCollection("rows").Insert(Row(2)); return tx; }).Result;
                var queue = SharedHandleQueue.Of(ownerEngine);
                var before = db.GetSharedWaitDiagnostics().Total;
                var elapsed = Stopwatch.StartNew();
                var begin = Unmarked(() => Record.Exception(() => db.BeginTransaction().Dispose()));
                Assert.True(SharedHandleQueue.WaitForQueuedBegin(queue, TimeSpan.FromSeconds(10)), "The begin never queued locally.");
                if (!begin.Wait(Budget + TimeSpan.FromSeconds(5)))
                {
                    Unmarked(first.Commit).Wait();
                    Assert.Fail("The begin waited in the local queue past its budget.");
                }
                elapsed.Stop();
                var timeout = Assert.IsType<LiteException>(begin.Result);
                Assert.Equal(LiteException.LOCK_TIMEOUT, timeout.ErrorCode);
                Assert.Contains("transaction handle", timeout.Message);
                AssertWithinBudget(elapsed.Elapsed);
                // Nobody waits in the queue any more; it is still held by the first handle only.
                Assert.Equal(0, SharedHandleQueue.Waiters(queue));
                Assert.Equal(0, queue.CurrentCount);
                Assert.Equal(0, db.TransactionHandles.ActiveCount);
                Assert.True(FreeOnAnotherThread(TurnMutex(ownerEngine)), "The queued begin took the turnstile.");
                var after = db.GetSharedWaitDiagnostics();
                Assert.Equal((1, 1), (after.Total.Count - before.Count, after.Total.TimedOut - before.TimedOut));
                Assert.Equal(0, after.CurrentWaiters);

                Unmarked(first.Commit).Wait();
                Assert.Equal(1, queue.CurrentCount);
                Unmarked(() => { using var tx = db.BeginTransaction(); tx.GetCollection("rows").Insert(Row(3)); tx.Commit(); }).Wait();
                Unmarked(() => { using var tx = owner.BeginTransaction(); tx.GetCollection("rows").Insert(Row(4)); tx.Commit(); }).Wait();
                Assert.Equal(1, queue.CurrentCount);
            }
            Verify(file, null, 1, 2, 3, 4);
        }
    }
}
