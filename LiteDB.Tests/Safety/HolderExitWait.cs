using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using LiteDB.Client.Shared;

namespace LiteDB.Tests.Safety
{
    /// <summary>How a disposed Shared connection's mutex owner (holder) thread ended, as judged by <see cref="HolderExitWait"/>.</summary>
    internal sealed class HolderExitResult
    {
        /// <summary>The holder thread is still alive after the wait.</summary>
        public bool Alive { get; set; }
        /// <summary>The holder still owned the OS mutex: it cannot exit by itself.</summary>
        public bool HoldsMutex { get; set; }
        /// <summary>Commands sent to the holder after the wait began (each restarts its idle clock).</summary>
        public int Commands { get; set; }
        /// <summary>The holder outlived the design bound while idle and exited later (scheduling, not a leak).</summary>
        public bool LateIdleExit { get; set; }
        public double WaitedMs { get; set; }
        /// <summary>The violation, or null when the holder exited as designed.</summary>
        public string Violation { get; set; }
    }

    /// <summary>
    /// Waits for a disposed Shared connection's holder thread to exit, judged by the holder's own exit rule
    /// (<c>SharedMutexOwner.Run</c>): it exits on its first poll, after <c>HolderIdle</c> without a command, that
    /// finds no command pending and the OS mutex not held. So the design bound is <see cref="QuiescentProbe.Grace"/>
    /// (HolderIdle + 2 polls + an allowance). A holder still alive at that bound is a leak when it holds the mutex
    /// (it never exits) or received a command since the wait began (its idle clock restarted). An idle holder that
    /// received nothing exits on its next scheduled poll by construction: waiting for that observable exit up to
    /// <see cref="LateExitBound"/> is a scheduling allowance for a loaded host (load averages of 25-53 delayed it
    /// past the design bound), recorded as <see cref="HolderExitResult.LateIdleExit"/>, not a pass by wall time.
    /// </summary>
    internal static class HolderExitWait
    {
        /// <summary>
        /// Upper bound for an idle, unscheduled holder: the lock-bound deadline floor (<see cref="DeadlineWatchdog.MinimumLockBound"/>).
        /// Reached only when the host starves the holder that long, or a change stopped idle holders from exiting.
        /// </summary>
        public static readonly TimeSpan LateExitBound = DeadlineWatchdog.MinimumLockBound;
        private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(10);
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        public static HolderExitResult Wait(SharedMutexOwner owner) => Wait(owner, QuiescentProbe.Grace);

        /// <summary><paramref name="designBound"/> is a parameter only so the oracle self-test can force the late path.</summary>
        internal static HolderExitResult Wait(SharedMutexOwner owner, TimeSpan designBound)
        {
            var result = new HolderExitResult();
            var commands = 0;
            var previous = owner.BeforeNotify;
            Action counter = () =>
            {
                Interlocked.Increment(ref commands);
                previous?.Invoke();
            };
            // Runs under the owner's lock whenever a caller sends or posts a command.
            owner.BeforeNotify = counter;
            var waited = Stopwatch.StartNew();
            try
            {
                while (owner.HasHolderThread && waited.Elapsed < designBound) Join(owner, designBound - waited.Elapsed);
                if (owner.HasHolderThread)
                {
                    result.HoldsMutex = Read<bool>(owner, "_held");
                    while (!result.HoldsMutex && Volatile.Read(ref commands) == 0 && owner.HasHolderThread &&
                        waited.Elapsed < LateExitBound)
                    {
                        Join(owner, Slice);
                        result.HoldsMutex = owner.HasHolderThread && Read<bool>(owner, "_held");
                    }
                    result.LateIdleExit = !owner.HasHolderThread && !result.HoldsMutex && Volatile.Read(ref commands) == 0;
                }
            }
            finally
            {
                // Leave a hook another party installed meanwhile in place.
                if (ReferenceEquals(owner.BeforeNotify, counter)) owner.BeforeNotify = previous;
            }
            result.WaitedMs = waited.Elapsed.TotalMilliseconds;
            result.Alive = owner.HasHolderThread;
            result.Commands = Volatile.Read(ref commands);
            if (result.Alive && result.HoldsMutex)
                result.Violation = "threads: the mutex owner thread still holds the OS mutex, so it cannot exit";
            else if (result.Alive && result.Commands > 0)
                result.Violation = $"threads: the mutex owner thread outlived {designBound.TotalMilliseconds:F0} ms " +
                    $"and received {result.Commands} command(s) after Dispose";
            else if (result.Alive)
                result.Violation = $"threads: the idle mutex owner thread outlived {LateExitBound.TotalMilliseconds:F0} ms";
            return result;
        }

        private static void Join(SharedMutexOwner owner, TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero) return;
            var holder = Read<Thread>(owner, "_holder");
            if (holder == null) return;
            // The exit itself is the signal; a holder restarted by a new command is a new thread object.
            holder.Join(timeout < Slice ? timeout : Slice);
        }

        private static T Read<T>(SharedMutexOwner owner, string name)
        {
            var field = typeof(SharedMutexOwner).GetField(name, Private)
                ?? throw new InvalidOperationException($"SharedMutexOwner.{name} no longer exists; update HolderExitWait.");
            var sync = typeof(SharedMutexOwner).GetField("_sync", Private)?.GetValue(owner)
                ?? throw new InvalidOperationException("SharedMutexOwner._sync no longer exists; update HolderExitWait.");
            lock (sync) return (T)field.GetValue(owner);
        }
    }
}
