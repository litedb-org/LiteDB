#if NET8_0_OR_GREATER
using System;
using System.Threading;
using System.Collections.Generic;
using LiteDB.Utils;

namespace LiteDB.Client.Shared
{
    // Advisory requests never grant storage access. Main + the real WRITE byte do.
    // Each process publishes exactly one local request; renewed leases make
    // long waits live, while a killed requester disappears after ExpiryMilliseconds.
    internal sealed class SharedWriterRequests
    {
        internal const int ExpiryMilliseconds = 100;
        private readonly SharedMemoryAuthority _authority;
        private readonly object _gate = new object();
        private readonly LinkedList<Lease> _queue = new LinkedList<Lease>();
        private long _ticket;
        private Timer _renewal;
        private Exception _failure;
        internal SharedWriterRequests(SharedMemoryAuthority authority) => _authority = authority;
        internal bool Pending { get { lock (_gate) return _queue.Count != 0; } }

        internal IDisposable Enter(Func<bool> cancelled = null)
        {
            var lease = new Lease(this);
            try
            {
                lock (_gate)
                {
                    lease.Node = _queue.AddLast(lease);
                    while (!ReferenceEquals(_queue.First.Value, lease))
                    {
                        RefuseCancelled(cancelled);
                        Monitor.Wait(_gate, 20);
                    }
                    RefuseCancelled(cancelled);
                    Publish();
                    _renewal = SharedBackground.CreateTimer(Renew, null, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25));
                }
                _authority.Map.Wake();
                Stage("request-published");
                while (true)
                {
                    RefuseCancelled(cancelled);
                    lock (_gate)
                    {
                        // An expired published request rejoins at a fresh position;
                        // neither a timer nor a resumed caller resurrects its old ticket.
                        if (_failure != null) throw new InvalidOperationException("Shared writer request renewal failed.", _failure);
                        Refresh();
                        var now = Environment.TickCount64;
                        Stage("request-sampled");
                        if (IsTurn(now)) break;
                    }
                    Thread.Sleep(1);
                }
                Stage("request-admitted");
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        private static void RefuseCancelled(Func<bool> cancelled)
        {
            if (cancelled?.Invoke() == true) throw new ObjectDisposedException("SharedEngine");
        }

        private void Publish()
        {
            _ticket = _authority.Map.Increment(SharedMemoryLayout.RequestSequence);
            if (_ticket <= 0) throw new InvalidOperationException("Shared writer request sequence exhausted.");
            _authority.Map.Store(_authority.Block + SharedMemoryLayout.RequestExpiry,
                Environment.TickCount64 + ExpiryMilliseconds);
            _authority.Map.Store(_authority.Block + SharedMemoryLayout.TenureRequest, _ticket);
        }

        private void Refresh()
        {
            if (_authority.Map.Load(_authority.Block + SharedMemoryLayout.RequestExpiry) <= Environment.TickCount64)
                Publish();
            else
                _authority.Map.Store(_authority.Block + SharedMemoryLayout.RequestExpiry,
                    Environment.TickCount64 + ExpiryMilliseconds);
        }

        private void Renew(object unused)
        {
            lock (_gate)
            {
                if (_ticket == 0) return;
                try { Refresh(); }
                catch (Exception ex)
                {
                    // Timer callbacks cannot surface failures on the application
                    // thread. Withdraw priority and let its waiting call report it.
                    _failure = ex;
                    Retire();
                }
            }
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
            // Load expiry before sampling the upper bound: a peer can legitimately
            // renew after our caller sampled now and was descheduled.
            return expiry > now && expiry - Environment.TickCount64 <= ExpiryMilliseconds;
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

        private void Exit(Lease lease)
        {
            lock (_gate)
            {
                if (lease.Node == null) return;
                var published = ReferenceEquals(_queue.First, lease.Node);
                _queue.Remove(lease.Node);
                lease.Node = null;
                if (published)
                {
                    // Retire this request even with local waiters. Only their own
                    // publication can establish the next process ticket.
                    Retire();
                    _failure = null;
                }
                Monitor.PulseAll(_gate);
            }
        }

        private void Retire()
        {
            _renewal?.Dispose();
            _renewal = null;
            _ticket = 0;
            _authority.Map.Store(_authority.Block + SharedMemoryLayout.TenureRequest, 0);
            _authority.Map.Store(_authority.Block + SharedMemoryLayout.RequestExpiry, 0);
        }

#if DEBUG || TESTING
        internal int WaitingCount { get { lock (_gate) return _queue.Count; } }
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
            internal LinkedListNode<Lease> Node;
            internal Lease(SharedWriterRequests requests) => _requests = requests;
            public void Dispose() => Interlocked.Exchange(ref _requests, null)?.Exit(this);
        }
    }
}
#endif
