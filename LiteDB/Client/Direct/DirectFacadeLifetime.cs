using System;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Direct
{
    // Only file-backed facades are finalizable; custom-stream engines retain
    // their previous lifetime. No operation touches this object.
    internal sealed class DirectFacadeLifetime : IDisposable
    {
        private DirectProcessHost _host;
        internal TransactionMonitor Owner;
        internal DirectFacadeLifetime(DirectProcessHost host, TransactionMonitor owner)
        { _host = host; Owner = owner; }

        public void Dispose()
        {
            var host = Interlocked.Exchange(ref _host, null);
            if (host != null) host.Release(Owner);
            GC.SuppressFinalize(this);
        }

        ~DirectFacadeLifetime()
        {
            try { this.Dispose(); }
            catch { /* Existing stream/native finalizers finish failed cleanup. */ }
        }
    }
}
