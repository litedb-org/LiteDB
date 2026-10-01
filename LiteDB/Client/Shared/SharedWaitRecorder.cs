using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Records one Shared connection's waits for writer ownership: totals since creation and
    /// per-minute buckets for the last hour. A wait is recorded only if it actually blocked
    /// for ownership; nothing is allocated per wait except a slow-wait notification.
    /// </summary>
    internal sealed class SharedWaitRecorder
    {
        private const int Minutes = 60;
        private static readonly long Ticks500 = Stopwatch.Frequency / 2;
        private static readonly long Ticks1000 = Stopwatch.Frequency;

        private readonly object _sync = new object();
        private readonly string _mutexName;
        private readonly string _filename;
        private readonly TimeSpan _slowThreshold;
        // Weak: a handle's holder shares this recorder and must not root the application's
        // observer (and through it a database or handle). The connection's settings keep it alive.
        private readonly WeakReference<Action<SharedSlowWait>> _slowWait;
        private readonly long _created = Stopwatch.GetTimestamp();
        private readonly List<long> _active = new List<long>();
        private readonly Bucket[] _minutes = new Bucket[Minutes];
        private Bucket _total;

        private struct Bucket
        {
            internal long Minute, Count, Ticks, Max, Over500, Over1000, TimedOut, Refused;

            internal void Add(long elapsed, bool timedOut)
            {
                Count++;
                Ticks += elapsed;
                if (elapsed > Max) Max = elapsed;
                if (elapsed > Ticks500) Over500++;
                if (elapsed > Ticks1000) Over1000++;
                if (timedOut) TimedOut++;
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
        {
            _mutexName = mutexName;
            _filename = filename;
            _slowThreshold = slowThreshold;
            _slowWait = slowWait == null ? null : new WeakReference<Action<SharedSlowWait>>(slowWait);
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
            var start = Stopwatch.GetTimestamp();
            lock (_sync) _active.Add(start);
            return new Wait(start, SharedHandleRegistry.Owner(_mutexName) != null);
        }

        internal void End(Wait wait, bool timedOut)
        {
            var start = wait.Start;
            var now = Stopwatch.GetTimestamp();
            var elapsed = now - start;
            lock (_sync)
            {
                _active.Remove(start);
                _total.Add(elapsed, timedOut);
                this.Current(now).Add(elapsed, timedOut);
            }
            if (_slowWait == null || _slowThreshold == Timeout.InfiniteTimeSpan || ToTime(elapsed) < _slowThreshold ||
                !_slowWait.TryGetTarget(out var observer)) return;
            var info = new SharedSlowWait(_filename, ToTime(elapsed), timedOut,
                wait.HandleOwner ? SharedWriterOwner.TransactionHandle : SharedWriterOwner.Unknown);
            // The caller may own the native mutex now: never run application code here.
            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                try { observer((SharedSlowWait)state); }
                catch (Exception) { /* A diagnostic observer must not affect the database. */ }
            }, info);
        }

        internal void Refused()
        {
            var now = Stopwatch.GetTimestamp();
            lock (_sync)
            {
                _total.Refused++;
                this.Current(now).Refused++;
            }
        }

        internal SharedWaitDiagnostics Snapshot(TimeSpan window)
        {
            var minutes = (int)Math.Max(1, Math.Min(Minutes, Math.Ceiling(window.TotalMinutes)));
            var now = Stopwatch.GetTimestamp();
            var minute = this.MinuteOf(now);
            Bucket recent = default, total;
            int waiters;
            long oldest = now;
            lock (_sync)
            {
                foreach (var bucket in _minutes)
                    if (bucket.Count + bucket.Refused != 0 && minute - bucket.Minute < minutes) recent.Merge(bucket);
                total = _total;
                waiters = _active.Count;
                foreach (var start in _active) if (start < oldest) oldest = start;
            }
            var owner = SharedHandleRegistry.Owner(_mutexName);
            return new SharedWaitDiagnostics(waiters, ToTime(now - oldest),
                owner == null ? SharedWriterOwner.Unknown : SharedWriterOwner.TransactionHandle,
                owner?.Held ?? TimeSpan.Zero, owner?.Idle ?? TimeSpan.Zero,
                TimeSpan.FromMinutes(minutes), recent.ToStatistics(), total.ToStatistics());
        }

        /// <summary>The message for a wait that ran out of <paramref name="timeout"/>.</summary>
        internal LiteException TimeoutError(TimeSpan timeout)
        {
            var owner = SharedHandleRegistry.Owner(_mutexName);
            var detail = owner == null
                ? "The owner is another connection or process."
                : $"It is owned by a transaction handle of this process, held for {owner.Held} and idle for {owner.Idle}.";
            return new LiteException(LiteException.LOCK_TIMEOUT,
                $"Shared writer ownership of '{_filename}' was not acquired within {timeout} (SharedWriterTimeout). {detail}");
        }

        private ref Bucket Current(long now)
        {
            var minute = this.MinuteOf(now);
            ref var bucket = ref _minutes[minute % Minutes];
            if (bucket.Minute != minute || bucket.Count + bucket.Refused == 0) bucket = new Bucket { Minute = minute };
            return ref bucket;
        }

        private long MinuteOf(long timestamp) => (timestamp - _created) / (Stopwatch.Frequency * 60);

        private static TimeSpan ToTime(long ticks) => TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);
    }
}
