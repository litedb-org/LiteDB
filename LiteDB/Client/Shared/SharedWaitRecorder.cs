using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Records one Shared connection's waits for writer ownership: totals since creation and
    /// per-minute buckets for the last hour (the current partial minute plus 60 whole ones).
    /// Every non-recursive acquisition is recorded, also one that did not have to block.
    /// <c>Count</c> holds the waits that ended by acquiring (or otherwise without timeout or
    /// refusal) or by timing out; a refused wait counts only in <c>Refused</c> and adds no wait
    /// time. Nothing is allocated per wait except a slow-wait notification.
    /// </summary>
    internal sealed class SharedWaitRecorder
    {
        private const int Minutes = 60;
        // The current partial minute plus Minutes whole ones.
        private const int Buckets = Minutes + 1;
        private static readonly long TicksPerMinute = Stopwatch.Frequency * 60;
        private static readonly Func<long> Clock = Stopwatch.GetTimestamp;
        private static readonly long Ticks500 = Stopwatch.Frequency / 2;
        private static readonly long Ticks1000 = Stopwatch.Frequency;

        private readonly object _sync = new object();
        private readonly string _mutexName;
        private readonly string _filename;
        private readonly TimeSpan _slowThreshold;
        // Weak: a handle's holder shares this recorder and must not root the application's
        // observer (and through it a database or handle). The connection's settings keep it alive.
        private readonly WeakReference<Action<SharedSlowWait>> _slowWait;
        private readonly Func<long> _timestamp;
        private readonly long _created;
        private readonly List<long> _active = new List<long>();
        private readonly Bucket[] _minutes = new Bucket[Buckets];
        private Bucket _total;

        private struct Bucket
        {
            internal long Minute, Count, Ticks, Max, Over500, Over1000, TimedOut, Refused;

            internal void Add(long elapsed, Outcome outcome)
            {
                if (outcome == Outcome.Refused)
                {
                    Refused++;
                    return;
                }
                Count++;
                Ticks += elapsed;
                if (elapsed > Max) Max = elapsed;
                if (elapsed > Ticks500) Over500++;
                if (elapsed > Ticks1000) Over1000++;
                if (outcome == Outcome.TimedOut) TimedOut++;
            }

            internal void Merge(Bucket other)
            {
                Count += other.Count;
                Ticks += other.Ticks;
                if (other.Max > Max) Max = other.Max;
                Over500 += other.Over500;
                Over1000 += other.Over1000;
                TimedOut += other.TimedOut;
                Refused += other.Refused;
            }

            internal SharedWaitStatistics ToStatistics() => new SharedWaitStatistics(Count, ToTime(Ticks), ToTime(Max), Over500, Over1000, TimedOut, Refused);
        }

        internal SharedWaitRecorder(string mutexName, string filename, TimeSpan slowThreshold, Action<SharedSlowWait> slowWait)
            : this(mutexName, filename, slowThreshold, slowWait, Clock)
        {
        }

        /// <summary>As above, reading time (Stopwatch ticks) from <paramref name="timestamp"/>.</summary>
        internal SharedWaitRecorder(string mutexName, string filename, TimeSpan slowThreshold, Action<SharedSlowWait> slowWait, Func<long> timestamp)
        {
            _timestamp = timestamp;
            _created = timestamp();
            _mutexName = mutexName;
            _filename = filename;
            _slowThreshold = slowThreshold;
            _slowWait = slowWait == null ? null : new WeakReference<Action<SharedSlowWait>>(slowWait);
        }

        /// <summary>How a recorded wait ended.</summary>
        internal enum Outcome
        {
            /// <summary>Ownership was acquired, or the wait ended by a failure other than a timeout or refusal.</summary>
            Acquired,
            /// <summary>The wait ran out of <c>SharedWriterTimeout</c>.</summary>
            TimedOut,
            /// <summary>The wait was refused (<c>SharedSelfWaitGrace</c>) after waiting at least one grace slice.</summary>
            Refused
        }

        /// <summary>A wait in progress: when it began and the owner known then.</summary>
        internal readonly struct Wait
        {
            internal readonly long Start;
            internal readonly bool HandleOwner;
            internal Wait(long start, bool handleOwner) { Start = start; HandleOwner = handleOwner; }
        }

        /// <summary>Register a wait that is about to block. Pass the result to <see cref="End"/>.</summary>
        internal Wait Begin()
        {
            var start = _timestamp();
            lock (_sync) _active.Add(start);
            return new Wait(start, SharedHandleRegistry.Owner(_mutexName) != null);
        }

        /// <summary>The recorder's clock, for a wait that ended on another thread (<see cref="End"/>'s <c>end</c>).</summary>
        internal long Now() => _timestamp();

        /// <summary>
        /// End a wait exactly once. A refusal counts only in <c>Refused</c>. <paramref name="end"/>, when
        /// not zero, is when the wait itself ended (<see cref="Now"/>), if the caller learns it later.
        /// </summary>
        internal void End(Wait wait, Outcome outcome, long end = 0)
        {
            var start = wait.Start;
            var now = end != 0 ? end : _timestamp();
            var elapsed = now - start;
            lock (_sync)
            {
                _active.Remove(start);
                _total.Add(elapsed, outcome);
                this.Current(now).Add(elapsed, outcome);
            }
            if (outcome == Outcome.Refused || _slowWait == null || _slowThreshold == Timeout.InfiniteTimeSpan || ToTime(elapsed) < _slowThreshold ||
                !_slowWait.TryGetTarget(out var observer)) return;
            Notify(observer, new SharedSlowWait(_filename, ToTime(elapsed), outcome == Outcome.TimedOut,
                wait.HandleOwner ? SharedWriterOwner.TransactionHandle : SharedWriterOwner.Unknown));
        }

        // Separate from End: the closure capturing the observer is then allocated only for a
        // slow wait, not on every acquisition.
        private static void Notify(Action<SharedSlowWait> observer, SharedSlowWait info)
        {
            // The caller may own the native mutex now: never run application code here.
            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                try { observer((SharedSlowWait)state); }
                catch (Exception) { /* A diagnostic observer must not affect the database. */ }
            }, info);
        }

        /// <summary>A refusal before any wait began (a zero <c>SharedSelfWaitGrace</c>).</summary>
        internal void Refused()
        {
            var now = _timestamp();
            lock (_sync)
            {
                _total.Refused++;
                this.Current(now).Refused++;
            }
        }

        /// <summary>
        /// A snapshot whose recent statistics cover the current partial minute plus N whole minutes,
        /// N = <paramref name="window"/> rounded up (1 to 60): at least the window, at most one minute more.
        /// </summary>
        internal SharedWaitDiagnostics Snapshot(TimeSpan window)
        {
            var minutes = (int)Math.Max(1, Math.Min(Minutes, Math.Ceiling(window.TotalMinutes)));
            var now = _timestamp();
            var minute = this.MinuteOf(now);
            var covered = TimeSpan.FromMinutes(minutes) + ToTime((now - _created) % TicksPerMinute);
            Bucket recent = default, total;
            int waiters;
            long oldest = now;
            lock (_sync)
            {
                foreach (var bucket in _minutes)
                    if (bucket.Count + bucket.Refused != 0 && minute - bucket.Minute <= minutes) recent.Merge(bucket);
                total = _total;
                waiters = _active.Count;
                foreach (var start in _active) if (start < oldest) oldest = start;
            }
            var owner = SharedHandleRegistry.Owner(_mutexName);
            return new SharedWaitDiagnostics(waiters, ToTime(now - oldest),
                owner == null ? SharedWriterOwner.Unknown : SharedWriterOwner.TransactionHandle,
                owner?.Held ?? TimeSpan.Zero, owner?.Idle ?? TimeSpan.Zero,
                covered, recent.ToStatistics(), total.ToStatistics());
        }

        /// <summary>
        /// The message for a wait that ran out of <paramref name="timeout"/>. It names, in this order:
        /// another thread of this connection it queued behind, this process's registered transaction
        /// handle, a handle of this process being admitted (its local queue is taken but it is not
        /// registered yet), and only otherwise another connection or process.
        /// </summary>
        internal LiteException TimeoutError(TimeSpan timeout, bool behindThisConnection, bool handleAdmitting)
        {
            var owner = SharedHandleRegistry.Owner(_mutexName);
            var handle = owner == null ? null : $"a transaction handle of this process, held for {owner.Held} and idle for {owner.Idle}";
            var detail = behindThisConnection
                ? "It waited behind another thread of this connection" + (handle == null ? "." : $"; the owner is {handle}.")
                : handle != null ? $"It is owned by {handle}."
                : handleAdmitting ? "The owner is this process's transaction handle (admitting), or another connection or process that handle waits for."
                : "The owner is another connection or process.";
            return new LiteException(LiteException.LOCK_TIMEOUT,
                $"Shared writer ownership of '{_filename}' was not acquired within {timeout} (SharedWriterTimeout). {detail}");
        }

        private ref Bucket Current(long now)
        {
            var minute = this.MinuteOf(now);
            ref var bucket = ref _minutes[minute % Buckets];
            if (bucket.Minute != minute || bucket.Count + bucket.Refused == 0) bucket = new Bucket { Minute = minute };
            return ref bucket;
        }

        private long MinuteOf(long timestamp) => (timestamp - _created) / TicksPerMinute;

        private static TimeSpan ToTime(long ticks) => TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);
    }
}
