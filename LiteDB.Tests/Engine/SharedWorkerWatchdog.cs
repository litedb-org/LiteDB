using System;
using System.Threading.Tasks;

namespace LiteDB.Tests.Engine;

internal static class SharedWorkerWatchdog
{
    // This measures individual worker liveness, not the throughput of a disk.
    // The test session retains its separate hard deadline. Stage changes and
    // peer progress must never extend this worker's completed-insert deadline.
    internal static async Task<bool> WaitAsync(Task worker, Func<long> completedIdleMilliseconds,
        int timeoutMilliseconds, Func<int, Task> delay = null)
    {
        if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        delay ??= milliseconds => Task.Delay(milliseconds);
        while (!worker.IsCompleted)
        {
            var idle = completedIdleMilliseconds();
            if (idle >= timeoutMilliseconds)
            {
                if (!worker.IsCompleted) return false;
                break;
            }
            await Task.WhenAny(worker, delay(timeoutMilliseconds - (int)idle));
            // Re-read both task state and progress after every timer wake-up:
            // an insert may have returned while the timer was being scheduled.
        }
        await worker;
        return true;
    }
}
