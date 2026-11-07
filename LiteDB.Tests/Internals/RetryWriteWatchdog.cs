#if !NETFRAMEWORK
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace LiteDB.Internals
{
    internal static class RetryWriteWatchdog
    {
        internal static readonly TimeSpan StallWindow = TimeSpan.FromSeconds(5);
        internal static readonly TimeSpan SafetyCap = TimeSpan.FromMinutes(2);

        internal static Task Wait(MvccProcess child, int first, int count) =>
            Wait(child.ReadLine, first, count, StallWindow, SafetyCap);

        // Only a completed write from this child advances the deadline. Unexpected,
        // duplicate and missing steps fail rather than acting as heartbeats.
        internal static async Task Wait(Func<TimeSpan, Task<string>> read, int first, int count,
            TimeSpan stallWindow, TimeSpan safetyCap)
        {
            var elapsed = Stopwatch.StartNew();
            var lastProgress = "none";
            var completedAt = TimeSpan.Zero;
            try
            {
                for (var step = 0; step <= count; step++)
                {
                    var remaining = safetyCap - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero) throw new TimeoutException("Overall safety cap reached");
                    var line = await read(remaining < stallWindow ? remaining : stallWindow);
                    if (elapsed.Elapsed >= safetyCap) throw new TimeoutException("Overall safety cap reached");
                    var expected = step == count ? "done" : "write:" + (first + step);
                    if (line != expected)
                        throw new InvalidOperationException($"Expected '{expected}', received '{line ?? "<EOF>"}'");
                    if (step < count)
                    {
                        lastProgress = line;
                        completedAt = elapsed.Elapsed;
                    }
                }
            }
            catch (Exception error)
            {
                // External stack tools are optional dependencies; always retain completed-work
                // evidence in the failure output before the caller disposes/kills the child.
                throw new InvalidOperationException(
                    $"Child write watchdog failed: last completed step={lastProgress}, " +
                    $"completed at={completedAt}, elapsed={elapsed.Elapsed}, " +
                    $"stall window={stallWindow}, safety cap={safetyCap}.\n" +
                    $"Parent stack before child cleanup:\n{Environment.StackTrace}", error);
            }
        }
    }
}
#endif
