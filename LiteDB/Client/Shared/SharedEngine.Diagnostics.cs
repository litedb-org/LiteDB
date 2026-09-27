using System.Threading;
using LiteDB.Client.Shared;

namespace LiteDB
{
    public partial class SharedEngine
    {
        internal long CoordinatedReadHits;
        private long _coordinatedReadMisses;
        private long _writerPressureRequests;
        private long _writerYields;

        /// <summary>
        /// Observe this connection without opening or mutating the database. Counters
        /// are cumulative; simultaneous operations can change them during observation.
        /// </summary>
        public SharedDiagnostics GetDiagnostics()
        {
            var path = "protected";
#if NET8_0_OR_GREATER
            lock (_snapshotGate)
                path = _coordination != null ? (_coordination.IsRevoked ? "revoked" : "mapped") :
                    _coordinationDemand == 0 && CoordinationFallbackReason == null ? "uninitialized" : "protected";
#endif
            if (Volatile.Read(ref _disposed) != 0) path = "disposed";
            return new SharedDiagnostics
            {
                ReadPath = path,
#if NET8_0_OR_GREATER
                FallbackReason = CoordinationFallbackReason,
#else
                FallbackReason = "runtime: mapped reads require .NET 8 or later",
#endif
                CoordinatedReadHits = Interlocked.Read(ref CoordinatedReadHits),
                CoordinatedReadMisses = Interlocked.Read(ref _coordinatedReadMisses),
                ActiveSnapshotLeases = _readers.ActiveLeases,
                ProcessMappedParticipants = SharedCoordinationEvents.Participants(_settings.Filename),
                WriterPressureRequests = Interlocked.Read(ref _writerPressureRequests),
                WriterYields = Interlocked.Read(ref _writerYields)
            };
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
