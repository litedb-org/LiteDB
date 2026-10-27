#if NET8_0_OR_GREATER
using System;
using System.Threading;
using LiteDB.Utils;

namespace LiteDB.Client.Shared
{
    // Advisory requests never grant storage access. Main + the real WRITE byte do.
    // Each process publishes its oldest outstanding request; renewed leases make
    // long waits live, while a killed requester disappears after ExpiryMilliseconds.
    internal sealed class SharedWriterRequests
    {
        internal const int ExpiryMilliseconds = 100;
        private readonly SharedMemoryAuthority _authority;
        private readonly object _gate = new object();
        private int _users;
        private long _ticket;
        private Timer _renewal;
        internal SharedWriterRequests(SharedMemoryAuthority authority) => _authority = authority;
        internal bool Pending { get { lock (_gate) return _users != 0; } }

        internal IDisposable Enter()
        {
            lock (_gate)
            {
                if (_users++ == 0)
                {
                    try
                    {
                        _ticket = _authority.Map.Increment(SharedMemoryLayout.RequestSequence);
                        if (_ticket <= 0) throw new InvalidOperationException("Shared writer request sequence exhausted.");
                        Renew(null);
                        _authority.Map.Store(_authority.Block + SharedMemoryLayout.TenureRequest, _ticket);
                        _renewal = new Timer(Renew, null, 25, 25);
                    }
                    catch
                    {
                        _users = 0;
                        _authority.Map.Store(_authority.Block + SharedMemoryLayout.TenureRequest, 0);
                        _authority.Map.Store(_authority.Block + SharedMemoryLayout.RequestExpiry, 0);
                        throw;
                    }
                }
            }
            var lease = new Lease(this);
            try
            {
                _authority.Map.Wake();
                Stage("request-published");
                while (!IsTurn(Environment.TickCount64)) Thread.Sleep(1);
                Stage("request-admitted");
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        private void Renew(object unused)
        {
            lock (_gate)
                if (_users != 0)
                    _authority.Map.Store(_authority.Block + SharedMemoryLayout.RequestExpiry,
                        Environment.TickCount64 + ExpiryMilliseconds);
        }

        private bool IsTurn(long now)
        {
            for (var slot = 0; slot < _authority.Header.Processes; slot++)
            {
                if (slot == _authority.Slot) continue;
                var block = SharedMemoryLayout.Blocks + (long)slot * _authority.Header.Stride;
                var ticket = _authority.Map.Load(block + SharedMemoryLayout.TenureRequest);
                if (ticket > 0 && ticket < _ticket && _authority.Map.Load(block) == 1 &&
                    Unexpired(block, now)) return false;
            }
            return true;
        }

        private bool Unexpired(long block, long now)
        {
            var expiry = _authority.Map.Load(block + SharedMemoryLayout.RequestExpiry);
            return expiry > now && expiry - now <= ExpiryMilliseconds;
        }

        internal bool HasPressure(long now)
        {
            if (Pending) return true;
            for (var slot = 0; slot < _authority.Header.Processes; slot++)
            {
                if (slot == _authority.Slot) continue;
                var block = SharedMemoryLayout.Blocks + (long)slot * _authority.Header.Stride;
                if (_authority.Map.Load(block) != 1) continue;
                // Expired/dead requests never reserve a turn. An attached live peer
                // still enables bounded tenure, even if it currently only reads.
                if (_authority.Map.Load(block + SharedMemoryLayout.TenureRequest) > 0 &&
                    Unexpired(block, now)) return true;
                if (_authority.Locks.IsLive(slot)) return true;
            }
            return false;
        }

        private void Exit()
        {
            lock (_gate)
            {
                if (--_users != 0) return;
                _renewal.Dispose();
                _renewal = null;
                _authority.Map.Store(_authority.Block + SharedMemoryLayout.TenureRequest, 0);
                _authority.Map.Store(_authority.Block + SharedMemoryLayout.RequestExpiry, 0);
            }
        }

#if DEBUG || TESTING
        [ThreadStatic] internal static Action<string> RequestStage;
#endif
        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("TESTING")]
        private static void Stage(string stage)
        {
#if DEBUG || TESTING
            Reachability.FaultPoint("RequestStage");
            RequestStage?.Invoke(stage);
#endif
        }
        private sealed class Lease : IDisposable
        {
            private SharedWriterRequests _requests;
            internal Lease(SharedWriterRequests requests) => _requests = requests;
            public void Dispose() => Interlocked.Exchange(ref _requests, null)?.Exit();
        }
    }
}
#endif
