#if NET8_0_OR_GREATER
using System;
using System.Threading;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB.Client.Shared
{
    // This is only admission metadata, never a substitute for validated DB/WAL.
    // The participant owns the mapping throughout every cache/result lifetime.
    internal sealed class SharedMemoryReadAdmission
    {
        private readonly SharedMemoryAuthority _authority;
        private static int _requests;
        internal SharedMemoryReadAdmission(SharedMemoryAuthority authority) => _authority = authority;

        internal bool TryRead(out SharedCoordinationStatus status)
        {
            status = default;
            var map = _authority.Map;
            var sequence = map.Load(SharedMemoryLayout.Control + 8);
            Stage("read-sequence");
            if (sequence <= 0 || (sequence & 1) != 0 || !Available()) return false;
            Stage("read-authority");
            var version = map.Load(SharedMemoryLayout.CommittedVersion);
            var structural = map.Load(SharedMemoryLayout.StructuralEpoch);
            var reuse = map.Load(SharedMemoryLayout.ReuseEpoch);
            var reset = map.Load(SharedMemoryLayout.ResetEpoch);
            var tenure = map.Load(SharedMemoryLayout.WriterTenureEpoch);
            Stage("read-fields");
            Interlocked.MemoryBarrier();
            Stage("read-recheck");
            if (version < 0 || version > int.MaxValue || structural < 0 || (structural & 1) != 0 ||
                reuse < 0 || reset < 0 || tenure <= 0 || !Available() ||
                sequence != map.Load(SharedMemoryLayout.Control + 8)) return false;
            status = new SharedCoordinationStatus(version, structural, reuse, reset, tenure, sequence);
            return true;
        }

        private bool Available()
        {
            var map = _authority.Map;
            return map.Load(SharedMemoryLayout.Control) == 1 &&
                map.Load(SharedMemoryLayout.FastAdmissionDisabled) == 0 &&
                map.Load(SharedMemoryLayout.RecoveryOwner) == 0 &&
                map.Matches(0, _authority.Header.Bytes) &&
                map.Matches(SharedMemoryLayout.Page, _authority.Header.Bytes);
        }

        // Advisory only: stale/dead requests expire, never reader generations.
        internal long RequestWriterTurn(long now)
        {
            var request = unchecked((long)((ulong)(uint)(now + 100) << 32) |
                (uint)(Interlocked.Increment(ref _requests) << 1) | 1L);
            _authority.Map.Store(_authority.Block + SharedMemoryLayout.RequestOffset, request);
            return request;
        }
        internal void EndWriterTurn(long request)
        {
            if (request != 0) _authority.Map.CompareExchange(_authority.Block + SharedMemoryLayout.RequestOffset, 0, request);
        }
        internal bool ShouldYieldToWriter(long now)
        {
            if (_authority.Map.Load(SharedMemoryLayout.WriterOwner) != 0) return true;
            for (var process = 0; process < _authority.Header.Processes; process++)
            {
                var request = _authority.Map.Load(SharedMemoryLayout.Blocks + (long)process * _authority.Header.Stride + SharedMemoryLayout.RequestOffset);
                var remaining = unchecked((uint)(request >> 32) - (uint)now);
                if ((request & 1) != 0 && remaining > 0 && remaining <= 100) return true;
            }
            return false;
        }
#if DEBUG || TESTING
        [ThreadStatic] internal static Action<string> AdmissionStage;
#endif
        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("TESTING")]
        internal static void Stage(string stage)
        {
#if DEBUG || TESTING
            Reachability.FaultPoint("AdmissionStage");
            AdmissionStage?.Invoke(stage);
#endif
        }
    }
}
#endif
