using System;
using System.Diagnostics;
using System.Threading;

namespace LiteDB.Engine
{
    // Installed only with an eligible Direct lifetime. No I/O, callbacks or
    // per-frame allocation; WAL writers publish successful physical extents.
    internal sealed class BackgroundCheckpointMetrics
    {
        private long _peakPhysical, _peakLogical, _requests, _passes, _activeTicks;
        private readonly long _preamble;

        internal BackgroundCheckpointMetrics(long physicalBytes, bool encrypted)
        {
            _preamble = encrypted ? Constants.PAGE_SIZE : 0;
            _peakPhysical = physicalBytes;
        }

        internal long PeakPhysicalBytes => Interlocked.Read(ref _peakPhysical);
        internal long PeakLogicalPages => Interlocked.Read(ref _peakLogical);
        internal long Requests => Interlocked.Read(ref _requests);
        internal long Passes => Interlocked.Read(ref _passes);
        internal double ActiveMilliseconds => Interlocked.Read(ref _activeTicks) * 1000.0 / Stopwatch.Frequency;
        internal void Request() => Interlocked.Increment(ref _requests);
        internal void Pass(long started)
        {
            Interlocked.Increment(ref _passes);
            Interlocked.Add(ref _activeTicks, Stopwatch.GetTimestamp() - started);
        }

        // Input excludes the encryption preamble, just like RawStream.Position.
        internal void ObservePhysical(long bytes) => Maximum(ref _peakPhysical, bytes == 0 ? 0 : bytes + _preamble);
        internal void ObserveLogical(long bytes) => Maximum(ref _peakLogical, bytes / Constants.PAGE_SIZE);

        private static void Maximum(ref long target, long value)
        {
            var previous = Interlocked.Read(ref target);
            while (value > previous)
            {
                var observed = Interlocked.CompareExchange(ref target, value, previous);
                if (observed == previous) return;
                previous = observed;
            }
        }
    }
}
