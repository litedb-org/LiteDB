#if NET8_0_OR_GREATER
using System;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    // A read-only bootstrap cannot certify writable recovery. Such snapshots may
    // still be reused under main, provided no writer tenure or storage fence has
    // changed. They never grant fast admission and never retain a reader lease.
    internal sealed class SharedProtectedSnapshot : IDisposable
    {
        private readonly SharedMemoryAuthority _authority;
        private LiteEngine _engine;
        private long _sequence, _tenure, _reuse, _reset, _structural;
        internal SharedProtectedSnapshot(SharedMemoryAuthority authority) => _authority = authority;

        // Both operations require the named database mutex. Participant final close
        // runs only once every facade and escaping result has released its edge.
        internal LiteEngine Take(bool abandoned)
        {
            var core = _engine;
            _engine = null;
            if (core == null) return null;
            var map = _authority.Map;
            if (!abandoned && core.CanRetainSharedSnapshot &&
                _sequence == map.Load(SharedMemoryLayout.Control + 8) &&
                _tenure == map.Load(SharedMemoryLayout.WriterTenureEpoch) &&
                _reuse == map.Load(SharedMemoryLayout.ReuseEpoch) &&
                _reset == map.Load(SharedMemoryLayout.ResetEpoch) &&
                _structural == map.Load(SharedMemoryLayout.StructuralEpoch)) return core;
            core.Dispose();
            return null;
        }

        internal void Park(LiteEngine core)
        {
            Dispose();
            var map = _authority.Map;
            _sequence = map.Load(SharedMemoryLayout.Control + 8);
            _tenure = map.Load(SharedMemoryLayout.WriterTenureEpoch);
            _reuse = map.Load(SharedMemoryLayout.ReuseEpoch);
            _reset = map.Load(SharedMemoryLayout.ResetEpoch);
            _structural = map.Load(SharedMemoryLayout.StructuralEpoch);
            _engine = core;
        }

        public void Dispose()
        {
            var core = _engine;
            _engine = null;
            core?.Dispose();
        }
    }
}
#endif
