using System;
using System.Runtime.CompilerServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Utils;
using Xunit;

namespace LiteDB.Tests.Utils
{
    /// <summary>
    /// The wait-for graph is bookkeeping only: it must not change what the garbage collector can reclaim.
    /// A finished thread's history must not root the thread (whose execution context keeps its AsyncLocal
    /// values), and a hold that is never released (an abandoned transaction) must not let the live thread
    /// that took it root the hold's owner.
    /// </summary>
    public class WaitGraphRetention_Tests : IDisposable
    {
        private readonly IDisposable _enabled = WaitGraph.Force();

        public void Dispose() => _enabled.Dispose();

        [Fact]
        public void A_finished_thread_with_lock_order_history_does_not_root_its_async_locals()
        {
            var ambient = RunThreadWithHistory();

            Collect(ambient);

            ambient.IsAlive.Should().BeFalse("the graph must not keep a finished thread's execution context alive");
        }

        [Fact]
        public void An_unreleased_hold_does_not_let_the_acquiring_thread_root_its_owner()
        {
            var owner = AcquireAndAbandon();

            Collect(owner);

            owner.IsAlive.Should().BeFalse("only the primitive (here unreachable) may keep its holds and their owners alive");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference RunThreadWithHistory()
        {
            WeakReference ambient = null;
            var thread = new Thread(() =>
            {
                var value = new object();
                ambient = new WeakReference(value);
                var local = new AsyncLocal<object> { Value = value };
                var first = WaitGraph.Create("test-retention-a", WaitPrimitive.Monitor);
                var second = WaitGraph.Create("test-retention-b", WaitPrimitive.Monitor);
                // An ordered pair: the lock-order history records this thread.
                WaitGraph.Acquired(first);
                WaitGraph.Acquired(second);
                WaitGraph.Released(second);
                WaitGraph.Released(first);
                GC.KeepAlive(local.Value);
            }) { IsBackground = true };
            thread.Start();
            thread.Join();
            return ambient;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference AcquireAndAbandon()
        {
            var owner = new object();
            var resource = WaitGraph.Create("test-retention-abandoned", WaitPrimitive.Condition);
            WaitGraph.Acquired(resource, owner, threadAffine: true);
            return new WeakReference(owner);
        }

        private static void Collect(WeakReference reference)
        {
            for (var attempt = 0; attempt < 20 && reference.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }
    }
}
