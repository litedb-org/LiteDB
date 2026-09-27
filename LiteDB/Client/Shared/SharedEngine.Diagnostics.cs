using System;
using System.Threading;
using LiteDB.Client.Shared;

namespace LiteDB
{
    public partial class SharedEngine
    {
        private long _coordinatedReadHits;
#if DEBUG || TESTING
        internal long CoordinatedReadHits => Interlocked.Read(ref _coordinatedReadHits);
#endif
        private long _coordinatedReadMisses;
        private long _writerPressureRequests;
        private long _writerYields;

        /// <summary>
        /// Observe this connection without opening or mutating the database. Counters
        /// are cumulative; simultaneous operations can change them during observation.
        /// </summary>
        public SharedDiagnostics GetDiagnostics()
        {
            var path = SharedReadPath.Protected;
#if NET8_0_OR_GREATER
            SharedCoordinationPage coordination;
            lock (_snapshotGate)
            {
                coordination = _coordination;
                if (_coordinationDemand == 0 && CoordinationFallbackReason == null) path = SharedReadPath.Uninitialized;
            }
            // IsRevoked synchronizes with page disposal itself. Filesystem probes must
            // not hold the snapshot gate used by query admission and idle retirement.
            if (coordination != null) path = coordination.IsRevoked ? SharedReadPath.Revoked : SharedReadPath.Mapped;
#endif
            if (Volatile.Read(ref _disposed) != 0) path = SharedReadPath.Disposed;
            return new SharedDiagnostics
            {
                ReadPath = path,
#if NET8_0_OR_GREATER
                FallbackReason = CoordinationFallbackReason,
#else
                FallbackReason = "runtime: mapped reads require .NET 8 or later",
#endif
                CoordinatedReadHits = Interlocked.Read(ref _coordinatedReadHits),
                CoordinatedReadMisses = Interlocked.Read(ref _coordinatedReadMisses),
                ActiveSnapshotLeases = _readers.ActiveLeases,
                ProcessMappedParticipants = SharedCoordinationEvents.Participants(_settings.Filename),
                WriterPressureRequests = Interlocked.Read(ref _writerPressureRequests),
                WriterYields = Interlocked.Read(ref _writerYields)
            };
        }

        private static string DescribeCoordinationFailure(Exception error)
        {
            var reason = error.GetType().Name + ": " + error.Message;
            if (error.InnerException != null) reason += " --> " + DescribeCoordinationFailure(error.InnerException);
            return reason;
        }

        private void RecordCoordinationFallback(string reason)
        {
            if (CoordinationFallbackReason == reason) return;
            CoordinationFallbackReason = reason;
            SharedCoordinationEvents.Log.Transition(_settings.Filename, "fallback", reason);
        }
#if NET8_0_OR_GREATER
        private long RequestWriterPressure()
        {
            var request = _coordination?.RequestWriterTurn(System.Environment.TickCount64) ?? 0;
            if (request != 0) Interlocked.Increment(ref _writerPressureRequests);
            return request;
        }
#endif
    }
}
