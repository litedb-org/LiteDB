using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Tests.Engine.SharedWaitGuards_Tests;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A legacy BeginTrans waiter under SharedWriterTimeout, and the self-wait marker of a handle
    /// that was collected without being completed (#3080).
    /// </summary>
    public class SharedWaitGuardsLegacy_Tests
    {
        private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);

#pragma warning disable CS0618
        [Theory]
        [InlineData(false, null)]
        [InlineData(true, "secret")]
        public void Legacy_BeginTrans_waiter_times_out_without_side_effects(bool sameConnection, string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using (var db = new LiteDatabase(new SharedEngine(Settings(file, password, Budget))))
            using (var other = new LiteDatabase(new SharedEngine(Settings(file, password))))
            {
                db.GetCollection("rows").FindById(1);
                // A handle of this connection, or of another one, owns the native writer mutex.
                var owner = sameConnection ? db : other;
                var tx = Unmarked(() => { var t = owner.BeginTransaction(); t.GetCollection("rows").Insert(Row(2)); return t; }).Result;
                var before = db.GetSharedWaitDiagnostics().Total;
                Exception error = null, completion = null;
                var reachedInsert = false;
                bool? rolledBack = null, committed = null;
                var elapsed = new Stopwatch();
                // A legacy transaction belongs to its thread: begin, insert and complete on one thread.
                var legacy = new Thread(() =>
                {
                    elapsed.Start();
                    error = Record.Exception(() =>
                    {
                        // In Shared mode BeginTrans itself acquires writer ownership, so it is what waits.
                        db.BeginTrans();
                        reachedInsert = true;
                        db.GetCollection("rows").Insert(Row(3));
                    });
                    elapsed.Stop();
                    // No legacy transaction was left on this thread: there is nothing to complete.
                    completion = Record.Exception(() => { rolledBack = db.Rollback(); committed = db.Commit(); });
                }) { IsBackground = true };
                legacy.Start();
                if (!legacy.Join(Budget + TimeSpan.FromSeconds(5)))
                {
                    Unmarked(tx.Commit).Wait();
                    legacy.Join(TimeSpan.FromSeconds(20));
                    Assert.Fail("The legacy BeginTrans waited past its budget.");
                }
                var timeout = Assert.IsType<LiteException>(error);
                Assert.Equal(LiteException.LOCK_TIMEOUT, timeout.ErrorCode);
                Assert.Contains("transaction handle", timeout.Message);
                Assert.False(reachedInsert);
                Assert.True(elapsed.Elapsed >= Budget - TimeSpan.FromMilliseconds(50) && elapsed.Elapsed <= Budget + TimeSpan.FromMilliseconds(250),
                    $"Timed out after {elapsed.Elapsed}");
                Assert.Null(completion);
                Assert.Equal((false, false), (rolledBack, committed));
                var after = db.GetSharedWaitDiagnostics();
                Assert.Equal((1, 1), (after.Total.Count - before.Count, after.Total.TimedOut - before.TimedOut));
                Assert.Equal(0, after.CurrentWaiters);
                Assert.Equal(LiteTransactionState.Active, tx.State);

                Unmarked(tx.Commit).Wait();
                // A later legacy transaction on another thread begins afresh (true: not joined) and commits.
                Exception later = null;
                var next = new Thread(() => later = Record.Exception(() =>
                {
                    Assert.True(db.BeginTrans());
                    db.GetCollection("rows").Insert(Row(4));
                    Assert.True(db.Commit());
                })) { IsBackground = true };
                next.Start();
                Assert.True(next.Join(TimeSpan.FromSeconds(20)));
                Assert.Null(later);
            }
            Verify(file, password, 1, 2, 4);
        }
#pragma warning restore CS0618

        // Begins a handle in the caller's flow (marking it) and drops it and its database uncompleted.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Abandon(string file)
        {
            var db = new LiteDatabase(new SharedEngine(Settings(file, grace: Immediately)));
            var tx = db.BeginTransaction();
            tx.GetCollection("rows").Insert(Row(99));
            return new WeakReference(tx);
        }

        // Another flow's handle owns the mutex: this flow's write (a child task, so it inherits the
        // flow's markers) must wait for it, not be refused; it completes once that handle commits.
        private static void WriteWaitsBehindAnotherFlowsHandle(LiteDatabase db, LiteDatabase other, int ownerId, int id)
        {
            var held = Unmarked(() => { var t = other.BeginTransaction(); t.GetCollection("rows").Insert(Row(ownerId)); return t; }).Result;
            var write = Task.Run(() => Record.Exception(() => db.GetCollection("rows").Insert(Row(id))));
            Assert.True(SpinWait.SpinUntil(() => write.IsCompleted || db.GetSharedWaitDiagnostics().CurrentWaiters == 1, TimeSpan.FromSeconds(10)));
            var early = write.IsCompleted;
            Unmarked(held.Commit).Wait();
            Assert.True(write.Wait(TimeSpan.FromSeconds(20)));
            Assert.False(early, $"The write did not wait for the other flow's handle: {write.Result}");
            Assert.Null(write.Result);
        }

        [Fact]
        public void Marker_of_a_collected_handle_causes_no_false_refusal()
        {
            using var file = new TempFile();
            Seed(file);
            var first = Abandon(file);
            // The handle and its database become unreachable; the handle's finalizer then releases
            // writer ownership. This flow still carries the handle's marker.
            Assert.True(SpinWait.SpinUntil(() =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                return !first.IsAlive;
            }, TimeSpan.FromSeconds(20)), "The abandoned handle was never collected.");
            using (var db = new LiteDatabase(new SharedEngine(Settings(file, grace: Immediately))))
            using (var other = new LiteDatabase(new SharedEngine(Settings(file))))
            {
                Assert.True(SpinWait.SpinUntil(() => db.GetSharedWaitDiagnostics().Owner == SharedWriterOwner.Unknown, TimeSpan.FromSeconds(20)),
                    "The collected handle kept writer ownership.");
                WriteWaitsBehindAnotherFlowsHandle(db, other, 2, 3);

                // A new handle in the same flow works with a zero grace, and refuses this flow's own
                // calls while it is open (the guard is live).
                var second = db.BeginTransaction();
                second.GetCollection("rows").Insert(Row(4));
                var refused = Task.Run(() => Record.Exception(() => db.GetCollection("rows").Insert(Row(5))));
                AssertRefusedWithin(refused, second);
                Assert.IsType<InvalidOperationException>(refused.Result);
                second.Commit();

                // Completed, it marks nothing: this flow's calls run, and wait behind other flows again.
                db.GetCollection("rows").Insert(Row(6));
                WriteWaitsBehindAnotherFlowsHandle(db, other, 7, 8);
                Assert.Equal(1, db.GetSharedWaitDiagnostics().Total.Refused);
            }
            Verify(file, null, 1, 2, 3, 4, 6, 7, 8);
        }
    }
}
