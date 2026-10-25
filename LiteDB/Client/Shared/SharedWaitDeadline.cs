using System;
using System.Diagnostics;
using System.Threading;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// One monotonic budget for a wait for Shared writer ownership. Every stage of that wait
    /// (local gates, the turnstile, the native mutex, a pin's holder) spends the same budget.
    /// The default value is infinite, so existing waits keep blocking as before.
    /// </summary>
    internal readonly struct SharedWaitDeadline
    {
        // A handle's holder thread waits on behalf of the caller that began the handle.
        [ThreadStatic] private static long _inherited;
        [ThreadStatic] private static bool _hasInherited;

        private readonly long _end;
        internal readonly TimeSpan Timeout;

        private SharedWaitDeadline(TimeSpan timeout, long end)
        {
            Timeout = timeout;
            _end = end;
        }

        internal static readonly SharedWaitDeadline Infinite = default;

        /// <summary>A Shared wait setting: infinite, or 0 to Int32.MaxValue milliseconds.</summary>
        internal static bool IsValidTimeout(TimeSpan value) =>
            value == System.Threading.Timeout.InfiniteTimeSpan || (value >= TimeSpan.Zero && value.TotalMilliseconds <= int.MaxValue);

        internal bool IsInfinite => _end == 0;

        internal static SharedWaitDeadline Start(TimeSpan timeout)
        {
            if (_hasInherited) return new SharedWaitDeadline(timeout, _inherited);
            if (timeout == System.Threading.Timeout.InfiniteTimeSpan) return Infinite;
            var end = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            return new SharedWaitDeadline(timeout, end == 0 ? 1 : end);
        }

        /// <summary>Remaining milliseconds for a timed wait: -1 when infinite, else 0 or more.</summary>
        internal int RemainingMilliseconds
        {
            get
            {
                if (IsInfinite) return System.Threading.Timeout.Infinite;
                var remaining = (_end - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency;
                return remaining <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Ceiling(remaining));
            }
        }

        /// <summary>A wait slice no longer than <paramref name="slice"/> and the remaining budget.</summary>
        internal int Slice(TimeSpan slice)
        {
            var remaining = RemainingMilliseconds;
            var poll = (int)slice.TotalMilliseconds;
            return remaining < 0 || remaining > poll ? poll : remaining;
        }

        internal bool Expired => !IsInfinite && Stopwatch.GetTimestamp() >= _end;

        /// <summary>The earlier of this deadline and <paramref name="slice"/> from now; reports this timeout.</summary>
        internal SharedWaitDeadline Within(TimeSpan slice)
        {
            var end = Stopwatch.GetTimestamp() + (long)(slice.TotalSeconds * Stopwatch.Frequency);
            if (!IsInfinite && _end < end) end = _end;
            return new SharedWaitDeadline(Timeout, end == 0 ? 1 : end);
        }

        /// <param name="behindThisConnection">The wait queued behind another thread of the same connection.</param>
        internal void ThrowIfExpired(bool behindThisConnection = false)
        {
            if (Expired) throw new SharedWaitTimeoutException(behindThisConnection);
        }

        /// <summary>Make this deadline apply to Shared waits on the current (holder) thread.</summary>
        internal Inheritance Inherit() => new Inheritance(this);

        internal readonly struct Inheritance : IDisposable
        {
            private readonly bool _previous;
            private readonly long _previousEnd;
            internal Inheritance(SharedWaitDeadline deadline)
            {
                _previous = _hasInherited;
                _previousEnd = _inherited;
                _hasInherited = !deadline.IsInfinite || _hasInherited;
                if (!deadline.IsInfinite) _inherited = deadline._end;
            }
            public void Dispose()
            {
                _hasInherited = _previous;
                _inherited = _previousEnd;
            }
        }
    }

    /// <summary>A Shared ownership wait ran out of its budget, before any side effect.</summary>
    internal sealed class SharedWaitTimeoutException : TimeoutException
    {
        internal SharedWaitTimeoutException(bool behindThisConnection = false) : base("Shared writer ownership wait timed out.")
        {
            BehindThisConnection = behindThisConnection;
        }

        /// <summary>The budget ran out in a local stage, behind another thread of the same connection.</summary>
        internal bool BehindThisConnection { get; }
    }
}
