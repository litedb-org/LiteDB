using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>The pool of Shared transaction-handle holder threads (#3083), ported from JKamsker/LiteDB#133.</summary>
    [Collection(NativeFileSyncCollection.Name)]
    public class TransactionHolderScheduler_Tests
    {
        internal static void Idle(Thread thread) => Assert.True(SpinWait.SpinUntil(
            () => SharedHolderScheduler.IsIdle(thread), TimeSpan.FromSeconds(10)), "The holder thread did not return to the pool.");

        /// <summary>Start from an empty pool whose idle threads do not expire during the test.</summary>
        internal sealed class PoolScope : IDisposable
        {
            internal PoolScope(int idleMilliseconds = 60000)
            {
                SharedHolderScheduler.RetireIdleWorkers();
                SharedHolderScheduler.IdleWaitOverride = idleMilliseconds;
            }

            public void Dispose()
            {
                SharedHolderScheduler.IdleWaitOverride = null;
                SharedHolderScheduler.BeforeIdleExpiry = null;
                SharedHolderScheduler.RetireIdleWorkers();
            }
        }

        [Fact]
        public void Jobs_run_in_a_clean_context_and_cannot_leak_into_later_jobs()
        {
            using var pool = new PoolScope();
            var ambient = new AsyncLocal<string>();
            Exception error = null;
            Thread previous = null;
            var created = SharedHolderScheduler.Created;
            foreach (var suppress in new[] { false, false, true, false })
            {
                ambient.Value = "caller";
                using var done = new ManualResetEventSlim();
                Action action = () =>
                {
                    try
                    {
                        // Neither the caller's value nor an earlier job's mutation is visible.
                        Assert.Null(ambient.Value);
                        ambient.Value = "job";
                    }
                    catch (Exception failure) { error = failure; }
                    finally { done.Set(); }
                };
                Thread worker;
                if (suppress) using (ExecutionContext.SuppressFlow()) worker = SharedHolderScheduler.Queue(action);
                else worker = SharedHolderScheduler.Queue(action);
                Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
                Idle(worker);
                if (error != null) throw error;
                if (previous != null) Assert.Same(previous, worker);
                Assert.Equal("caller", ambient.Value);
                previous = worker;
            }
            Assert.Equal(1, SharedHolderScheduler.Created - created);
            ambient.Value = null;
        }

        [Fact]
        public void Busy_workers_are_never_capped_and_at_most_two_stay_idle()
        {
            using var pool = new PoolScope();
            using var entered = new CountdownEvent(3);
            using var release = new ManualResetEventSlim();
            using var done = new ManualResetEventSlim();
            Action block = () => { entered.Signal(); release.Wait(); };
            var busy = new[] { SharedHolderScheduler.Queue(block), SharedHolderScheduler.Queue(block), SharedHolderScheduler.Queue(block) };
            Thread fourth = null;
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                // Three holders are busy; a fourth job still starts at once.
                fourth = SharedHolderScheduler.Queue(done.Set);
                Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
            }
            finally { release.Set(); }
            foreach (var worker in new[] { busy[0], busy[1], busy[2], fourth })
                Assert.True(SpinWait.SpinUntil(() => !worker.IsAlive || SharedHolderScheduler.IsIdle(worker), TimeSpan.FromSeconds(10)));
            Assert.Equal(SharedHolderScheduler.MaximumIdle, SharedHolderScheduler.IdleCount);
            Assert.Equal(4 - SharedHolderScheduler.MaximumIdle, Array.FindAll(new[] { busy[0], busy[1], busy[2], fourth }, worker => !worker.IsAlive).Length);
        }

        [Fact]
        public void Completed_job_graph_and_ambient_value_are_collectible_while_the_worker_is_idle()
        {
            using var pool = new PoolScope();
            Thread worker = null;
            WeakReference[] graph = null;
            TransactionHandle_Tests.OnThread(() => graph = SubmitGraph(out worker));
            Idle(worker);
            for (var i = 0; i < 10 && Array.Exists(graph, reference => reference.IsAlive); i++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.All(graph, reference => Assert.False(reference.IsAlive));
            Assert.True(SharedHolderScheduler.IsIdle(worker));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] SubmitGraph(out Thread worker)
        {
            var captured = new object();
            var ambientValue = new object();
            var ambient = new AsyncLocal<object> { Value = ambientValue };
            using var done = new ManualResetEventSlim();
            worker = SharedHolderScheduler.Queue(() => { GC.KeepAlive(captured); done.Set(); });
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
            GC.KeepAlive(ambient.Value);
            return new[] { new WeakReference(captured), new WeakReference(ambientValue) };
        }

        [Fact]
        public async Task Dispatch_racing_idle_expiry_runs_exactly_once_on_another_worker()
        {
            using var pool = new PoolScope(idleMilliseconds: 1);
            using var expiring = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var attempting = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            using var start = new ManualResetEventSlim();
            var calls = 0;
            var original = SharedHolderScheduler.Queue(start.Wait);
            SharedHolderScheduler.BeforeIdleExpiry = thread =>
            { if (thread == original) { expiring.Set(); release.Wait(); } };
            start.Set();
            try
            {
                Assert.True(expiring.Wait(TimeSpan.FromSeconds(10)));
                var queued = Task.Run(() =>
                {
                    attempting.Set();
                    return SharedHolderScheduler.Queue(() => { Interlocked.Increment(ref calls); finished.Set(); });
                });
                Assert.True(attempting.Wait(TimeSpan.FromSeconds(10)));
                release.Set();
                var replacement = await queued;
                Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
                Assert.NotSame(original, replacement);
                Assert.True(original.Join(TimeSpan.FromSeconds(10)));
                Assert.Equal(1, calls);
            }
            finally { release.Set(); }
        }

        [Fact]
        public void Idle_worker_expires_and_counters_are_exact()
        {
            using var pool = new PoolScope();
            int created = SharedHolderScheduler.Created, reused = SharedHolderScheduler.Reused, expired = SharedHolderScheduler.Expired;
            using (var done = new ManualResetEventSlim())
            {
                var first = SharedHolderScheduler.Queue(done.Set);
                Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
                Idle(first);
                done.Reset();
                SharedHolderScheduler.IdleWaitOverride = 50;
                Assert.Same(first, SharedHolderScheduler.Queue(done.Set));
                Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
                // This idle period uses the short override: the worker exits on its own.
                Assert.True(first.Join(TimeSpan.FromSeconds(10)));
            }
            Assert.Equal(1, SharedHolderScheduler.Created - created);
            Assert.Equal(1, SharedHolderScheduler.Reused - reused);
            Assert.Equal(1, SharedHolderScheduler.Expired - expired);
            Assert.Equal(0, SharedHolderScheduler.IdleCount);
        }

        public enum Leak { MutexScope, CallFrame, Deadline, TransactionContext }

        [Theory]
        [InlineData(Leak.MutexScope)]
        [InlineData(Leak.CallFrame)]
        [InlineData(Leak.Deadline)]
        [InlineData(Leak.TransactionContext)]
        public void Worker_with_leaked_thread_state_exits_instead_of_rejoining_the_pool(Leak leak)
        {
            using var pool = new PoolScope();
            var name = "litedb-holder-leak-" + Guid.NewGuid().ToString("N");
            using var mutex = SharedMutexFactory.Create(name);
            using var turn = SharedMutexFactory.Create(name + ".Turn");
            var dirty = SharedHolderScheduler.DirtyExits;
            using var done = new ManualResetEventSlim();
            var worker = SharedHolderScheduler.Queue(() =>
            {
                switch (leak)
                {
                    case Leak.MutexScope:
                        Assert.True(new SharedMutexScope(mutex, new SharedMutexTurnstile(turn)).Take(block: true, out _));
                        break;
                    case Leak.CallFrame:
                        SharedCallFrames.Enter(name, new object(), () => true);
                        break;
                    case Leak.Deadline:
                        SharedWaitDeadline.Start(TimeSpan.FromSeconds(30)).Inherit();
                        break;
                    case Leak.TransactionContext:
                        TransactionContext.Enter(new TransactionContext(null));
                        break;
                }
                done.Set();
            });
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "A worker with leaked state rejoined the pool.");
            Assert.False(SharedHolderScheduler.IsIdle(worker));
            Assert.Equal(1, SharedHolderScheduler.DirtyExits - dirty);
            if (leak == Leak.MutexScope)
            {
                // The exited thread's ownership is abandoned: the next owner learns of it.
                using var observer = SharedMutexFactory.Create(name);
                TransactionHandle_Tests.OnThread(() =>
                {
                    Assert.Throws<AbandonedMutexException>(() => observer.WaitOne(TimeSpan.FromSeconds(10)));
                    observer.ReleaseMutex();
                });
            }
            // The next job runs on a fresh, clean worker.
            done.Reset();
            var next = SharedHolderScheduler.Queue(done.Set);
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
            Assert.NotSame(worker, next);
            Idle(next);
        }
    }
}
