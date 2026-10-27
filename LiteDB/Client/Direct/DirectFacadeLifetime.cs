using System;
using System.Threading;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB.Client.Direct
{
    // Only file-backed facades are finalizable; custom-stream engines retain
    // their previous lifetime. No operation touches this object.
    internal sealed class DirectFacadeLifetime : IDisposable
    {
        private static DirectFacadeLifetime _pending;
        private static readonly Timer Reaper = CreateReaper();
        private static readonly WaitCallback DrainCallback = _ => Drain();
        private DirectFacadeLifetime _next;
        private DirectProcessHost _host;
        private readonly object _releaseSync = new object();
        private Exception _releaseError;
        internal TransactionMonitor Owner;
        internal DirectFacadeLifetime(DirectProcessHost host, TransactionMonitor owner)
        { _host = host; Owner = owner; }

        private static Timer CreateReaper()
        {
            // The reaper must not retain an ambient request that could root a facade or
            // its database. Finalizer cleanup is published without taking a monitor.
            if (ExecutionContext.IsFlowSuppressed()) return new Timer(_ => Drain(), null, 20, 20);
            using (ExecutionContext.SuppressFlow()) return new Timer(_ => Drain(), null, 20, 20);
        }

        internal static void Drain()
        {
            var pending = Interlocked.Exchange(ref _pending, null);
            while (pending != null)
            {
                var next = pending._next;
                try { pending.ReleaseCore(); }
                catch { /* Abandoned cleanup cannot escape the reaper. */ }
                pending = next;
            }
        }

        public void Dispose()
        {
            try { this.ReleaseCore(); }
            finally { if (Volatile.Read(ref _host) == null) GC.SuppressFinalize(this); }
        }

        ~DirectFacadeLifetime()
        {
            // Never acquire the host gate or run storage cleanup on the finalizer thread.
            // The lifetime object roots the host until the normal-thread reaper drains it.
            Reachability.Sometimes("maintenance:direct-finalizer-release-queued");
            DirectFacadeLifetime head;
            do
            {
                head = Volatile.Read(ref _pending);
                _next = head;
            }
            while (Interlocked.CompareExchange(ref _pending, this, head) != head);
            GC.KeepAlive(Reaper);
            try { ThreadPool.UnsafeQueueUserWorkItem(DrainCallback, null); }
            catch { /* The timer remains as a bounded fallback when scheduling is unavailable. */ }
        }

        private void ReleaseCore()
        {
            lock (_releaseSync)
            {
                var host = _host;
                if (host != null)
                {
                    // Refuse a storage-lock self-wait before relinquishing the
                    // facade reference; a later non-reentrant close may retry.
                    host.ValidateRelease();
                    _host = null;
                    try { host.Release(Owner); }
                    catch (Exception error) { _releaseError = error; }
                }
                if (_releaseError != null) throw _releaseError;
            }
        }
    }
}
