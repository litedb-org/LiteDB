#if NET8_0_OR_GREATER
using System;
using System.Diagnostics;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Per-connection scheduling budget, used only while writers request CPU time.
    /// Callers serialize budget access with the snapshot gate and sleep outside it,
    /// before publishing a lease. No scheduling decision authorizes storage access.
    /// </summary>
    internal sealed class SharedReadPacer
    {
        private const double DelayPerWork = 7;
        private double _pendingMilliseconds;

        internal int ReserveDelay(bool pressure)
        {
            if (!pressure) { _pendingMilliseconds = 0; return 0; }
            var delay = (int)Math.Min(10, Math.Max(0, _pendingMilliseconds));
            _pendingMilliseconds -= delay;
            return delay;
        }

        internal void RecordWork(long ticks) => Adjust(ticks * (DelayPerWork * 1000.0) / Stopwatch.Frequency);

        // Credit the actual scheduling delay, including coarse OS timer granularity.
        // A long suspension must not accumulate unbounded credit or future pauses.
        internal void RecordDelay(int reservedMilliseconds, long ticks) =>
            Adjust(reservedMilliseconds - ticks * 1000.0 / Stopwatch.Frequency);

        private void Adjust(double milliseconds) =>
            _pendingMilliseconds = Math.Max(-50, Math.Min(50, _pendingMilliseconds + milliseconds));
    }

    /// <summary>Measure engine execution, excluding time the caller spends between rows.</summary>
    internal sealed class MeasuredSharedReader : IBsonDataReader
    {
        private readonly IBsonDataReader _reader;
        private readonly Action<long> _record;
        private long _ticks;
        private int _disposed;
        private int _reads;

        internal MeasuredSharedReader(IBsonDataReader reader, Action<long> record)
        { _reader = reader; _record = record; }

        public BsonValue this[string field] => _reader[field];
        public string Collection => _reader.Collection;
        public BsonValue Current => _reader.Current;
        public bool HasValues => _reader.HasValues;

        public bool Read()
        {
            var start = Stopwatch.GetTimestamp();
            try { return _reader.Read(); }
            finally
            {
                Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - start);
                // Cooperate between small batches rather than running an entire
                // streaming scan before another process gets a scheduling turn.
                if (++_reads % 16 == 0) Thread.Yield();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var start = Stopwatch.GetTimestamp();
            try { _reader.Dispose(); }
            finally { _record(Interlocked.Read(ref _ticks) + Stopwatch.GetTimestamp() - start); }
        }
    }
}
#endif
