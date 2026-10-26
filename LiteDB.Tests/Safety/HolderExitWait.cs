using System;
using System.Collections.Generic;
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
        /// <summary>The holder kept the OS mutex for no owner of the connection: it cannot exit by itself.</summary>
        public bool HoldsMutex { get; set; }
        /// <summary>Commands sent to the holder after the wait began (each restarts its idle clock).</summary>
        public int Commands { get; set; }
        /// <summary>The first commands, as <c>command@thread</c>, for diagnosis.</summary>
        public string[] CommandLog { get; set; } = new string[0];
        /// <summary>The holder exited later than the design bound after its last activity (host scheduling).</summary>
        public bool LateIdleExit { get; set; }
        public double WaitedMs { get; set; }
        /// <summary>The violation, or null when the holder exited as designed.</summary>
        public string Violation { get; set; }
    }

    /// <summary>
    /// Waits for a disposed Shared connection's holder thread to exit, judged by the holder's own exit rule
    /// (<c>SharedMutexOwner.Run</c>): it exits on its first poll that finds no command pending, the OS mutex not
    /// held, and <c>HolderIdle</c> passed since its last command. The design bound <see cref="QuiescentProbe.Grace"/>
    /// (HolderIdle + 2 polls + an allowance) therefore runs from the holder's last activity: the start of the wait,
    /// or the last command sent to it since. Commands after Dispose are by design: a call racing Dispose acquires
    /// the mutex before its admission refuses it (<c>SharedEngine.AdmitLocked</c>), and such calls are bounded by
    /// their own Deadline. Fails:
    /// - at the design bound after the last activity, when the holder still holds the OS mutex for no owner (a
    ///   lost release; it can never exit);
    /// - at <see cref="LateExitBound"/> after the wait began, when the holder is still alive for any reason.
    /// An idle holder that outlives the design bound and exits before that is waited for by joining its thread
    /// (the exit is the signal) and recorded as <see cref="HolderExitResult.LateIdleExit"/>: on hosts with load
    /// averages of 25-53 its next poll ran more than 500 ms late, which a fixed wall-time allowance cannot bound.
    /// </summary>
    internal static class HolderExitWait
    {
        /// <summary>
        /// Upper bound for the holder's exit after Dispose: the lock-bound deadline floor
        /// (<see cref="DeadlineWatchdog.MinimumLockBound"/>), which also bounds the calls racing Dispose.
        /// </summary>
        public static readonly TimeSpan LateExitBound = DeadlineWatchdog.MinimumLockBound;
        private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(10);
        private const int LoggedCommands = 8;
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

        public static HolderExitResult Wait(SharedMutexOwner owner) => Wait(owner, QuiescentProbe.Grace, LateExitBound);

        /// <summary>The bounds are parameters only so the oracle self-tests can force the late paths quickly.</summary>
        internal static HolderExitResult Wait(SharedMutexOwner owner, TimeSpan designBound, TimeSpan lateBound)
        {
            var result = new HolderExitResult();
            var waited = Stopwatch.StartNew();
            var commands = 0;
            var lastCommandTicks = 0L;
            var log = new List<string>();
            var previous = owner.BeforeNotify;
            Action counter = () =>
            {
                // Runs under the owner's lock right after a caller published its command.
                if (Interlocked.Increment(ref commands) <= LoggedCommands)
                    lock (log) log.Add($"{Read<object>(owner, "_command")}@{Thread.CurrentThread.Name ?? "#" + Environment.CurrentManagedThreadId}");
                Interlocked.Exchange(ref lastCommandTicks, waited.Elapsed.Ticks);
                previous?.Invoke();
            };
            owner.BeforeNotify = counter;
            try
            {
                while (owner.HasHolderThread && waited.Elapsed < lateBound)
                {
                    var idle = waited.Elapsed - TimeSpan.FromTicks(Interlocked.Read(ref lastCommandTicks));
                    if (idle > designBound && HoldsForNobody(owner))
                    {
                        result.HoldsMutex = true;
                        break;
                    }
                    var holder = Read<Thread>(owner, "_holder");
                    // The exit itself is the signal; a holder a later command restarts is a new thread object.
                    if (holder != null) holder.Join(Slice);
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
            lock (log) result.CommandLog = log.ToArray();
            var sinceActivity = waited.Elapsed - TimeSpan.FromTicks(Interlocked.Read(ref lastCommandTicks));
            result.LateIdleExit = !result.Alive && sinceActivity > designBound;
            var commandText = result.Commands == 0 ? "" : $"; {result.Commands} command(s) after Dispose: {string.Join(", ", result.CommandLog)}";
            if (result.HoldsMutex)
                result.Violation = $"threads: the mutex owner thread holds the OS mutex for no owner {designBound.TotalMilliseconds:F0} ms " +
                    "after its last command, so it cannot exit" + commandText;
            else if (result.Alive)
                result.Violation = $"threads: the mutex owner thread outlived {lateBound.TotalMilliseconds:F0} ms after Dispose" + commandText;
            return result;
        }

        /// <summary>The holder owns the OS mutex while no thread of the connection owns its ownership.</summary>
        private static bool HoldsForNobody(SharedMutexOwner owner) =>
            owner.HasHolderThread && Read<bool>(owner, "_held") && Read<Thread>(owner, "_owner") == null;

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
