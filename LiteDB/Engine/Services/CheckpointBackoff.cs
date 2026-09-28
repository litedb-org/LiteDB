using System;
using System.Diagnostics;

namespace LiteDB.Engine
{
    /// <summary>
    /// Rations the auto-checkpoint attempts that queue behind readers. Such an attempt stalls the
    /// committing thread and every new reader, so a long-lived reader must not make each commit pay for one.
    /// [ThreadSafe]
    /// </summary>
    internal sealed class CheckpointBackoff
    {
        private const int INITIAL_DELAY_MILLISECONDS = 50;
        private const int MAX_DELAY_MILLISECONDS = 1000;
        private const int MILLISECONDS_PER_SECOND = 1000;

        private static readonly long _initialDelay = ToTicks(INITIAL_DELAY_MILLISECONDS);
        private static readonly long _maxDelay = ToTicks(MAX_DELAY_MILLISECONDS);

        private readonly object _sync = new object();
        private long _delay;
        private long _retryAt;

#if TESTING
        internal Func<long> Timestamp { get; set; } = Stopwatch.GetTimestamp;

        internal int WaitingAttempts { get; private set; }
#else
        private static long Timestamp() => Stopwatch.GetTimestamp();
#endif

        /// <summary>
        /// Claim the next waiting attempt. The following one is scheduled up front, as if this one had
        /// failed, so concurrent committers cannot all wait at once; Reset() undoes it on success.
        /// </summary>
        public bool TryClaimWaitingAttempt()
        {
            lock (_sync)
            {
                var now = Timestamp();

                if (now < _retryAt) return false;

                _delay = _delay == 0 ? _initialDelay : Math.Min(_delay * 2, _maxDelay);
                _retryAt = now + _delay;
#if TESTING
                this.WaitingAttempts++;
#endif
                return true;
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _delay = 0;
                _retryAt = 0;
            }
        }

        private static long ToTicks(int milliseconds) => milliseconds * Stopwatch.Frequency / MILLISECONDS_PER_SECOND;
    }
}
