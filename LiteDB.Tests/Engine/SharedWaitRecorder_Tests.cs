using System;
using System.Diagnostics;
using System.Threading;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>The Shared wait recorder adds no allocation to an acquisition that was not slow.</summary>
    public class SharedWaitRecorder_Tests
    {
        private const int Pairs = 10_000;

        [Fact]
        public void End_without_a_slow_wait_allocates_nothing()
        {
#if NETCOREAPP
            Action<SharedSlowWait> observer = _ => { };
            // (a) no observer; (b) an observer whose threshold the waits never reach.
            var quiet = new SharedWaitRecorder("recorder-test-a-" + Guid.NewGuid().ToString("N"), "a.db", Timeout.InfiniteTimeSpan, null);
            var observed = new SharedWaitRecorder("recorder-test-b-" + Guid.NewGuid().ToString("N"), "b.db", TimeSpan.FromHours(1), observer);
            // Less than one byte per wait: the regression this guards against allocated 24 B per
            // wait. A one-off runtime allocation (tier-up, a minute bucket) is not per wait.
            var quietBytes = Allocated(quiet);
            var observedBytes = Allocated(observed);
            Assert.True(quietBytes < Pairs, $"{quietBytes} B over {Pairs} waits without an observer");
            Assert.True(observedBytes < Pairs, $"{observedBytes} B over {Pairs} waits below the threshold");

            // Positive control: the measurement sees a slow wait's notification.
            using var notified = new ManualResetEventSlim();
            // The recorder holds its observer weakly: keep it alive for the measurement.
            Action<SharedSlowWait> notify = _ => notified.Set();
            var slow = new SharedWaitRecorder("recorder-test-c-" + Guid.NewGuid().ToString("N"), "c.db", TimeSpan.FromMilliseconds(1), notify);
            Assert.True(Allocated(slow, pairs: 20, hold: TimeSpan.FromMilliseconds(2)) > 0, "The allocation measurement does not see the slow path's notification.");
            Assert.True(notified.Wait(TimeSpan.FromSeconds(10)));
            GC.KeepAlive(observer);
            GC.KeepAlive(notify);
#else
            // GC.GetAllocatedBytesForCurrentThread is not available on .NET Framework.
            return;
#endif
        }

#if NETCOREAPP
        // Begin/End pairs, each wait lasting at least `hold` (spinning on the clock: allocation-free).
        private static long Allocated(SharedWaitRecorder recorder, int pairs = Pairs, TimeSpan hold = default)
        {
            var holdTicks = (long)(hold.TotalSeconds * Stopwatch.Frequency);
            // Warm up: JIT, the active list's capacity and the minute buckets.
            for (var i = 0; i < 100; i++) recorder.End(recorder.Begin(), SharedWaitRecorder.Outcome.Acquired);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < pairs; i++)
            {
                var wait = recorder.Begin();
                while (Stopwatch.GetTimestamp() - wait.Start < holdTicks) { }
                recorder.End(wait, SharedWaitRecorder.Outcome.Acquired);
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
#endif
    }
}
