using System;
using System.Runtime.InteropServices;
using System.Threading;
using LiteDB.Utils;

namespace LiteDB.Client.Shared
{
    // The handle roots the resource graph, never the escaping SharedDataReader.
    // In particular, inner reader/page finalizers must not run before disposal.
    // Only engine-created readers use this owner: their callbacks cannot capture
    // the escaping wrapper. Public caller-supplied callbacks have no such contract.
    internal sealed class SharedReaderCleanup : IDisposable
    {
        private static SharedReaderCleanup _pending;
        private static readonly Timer Reaper = CreateReaper();
        private SharedReaderCleanup _next;
        private readonly IBsonDataReader _reader;
        private readonly Action _release;
        private readonly string _namespace;
        private readonly object _connection;
        private readonly Func<bool> _retains;
        private GCHandle _root;
        private int _disposed;

        private static Timer CreateReaper()
        {
            // A process-wide reaper must not retain its first caller's ambient
            // ExecutionContext (which can itself contain a facade or request).
            if (ExecutionContext.IsFlowSuppressed()) return new Timer(_ => Drain(), null, 20, 20);
            using (ExecutionContext.SuppressFlow()) return new Timer(_ => Drain(), null, 20, 20);
        }

        internal SharedReaderCleanup(IBsonDataReader reader, Action release,
            string ns = null, object connection = null, Func<bool> retains = null)
        {
            _reader = reader;
            _release = release;
            _namespace = ns;
            _connection = connection;
            _retains = retains;
            _root = GCHandle.Alloc(this);
            GC.KeepAlive(Reaper);
        }

        // Finalizer thread: no disposal, monitor, I/O, callbacks or scheduler wait.
        internal void Abandon()
        {
            SharedReaderCleanup head;
            do
            {
                head = Volatile.Read(ref _pending);
                _next = head;
            }
            while (Interlocked.CompareExchange(ref _pending, this, head) != head);
        }

        internal static void Drain()
        {
            var pending = Interlocked.Exchange(ref _pending, null);
            while (pending != null)
            {
                var next = pending._next;
                try
                {
                    Reachability.Sometimes("maintenance:shared-abandoned-reader-cleanup");
                    pending.Dispose();
                }
                catch (Exception) { /* Abandoned cleanup cannot escape the reaper. */ }
                pending = next;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                if (_retains == null) Close();
                else using (SharedCallFrames.Enter(_namespace, _connection, _retains)) Close();
            }
            finally { _root.Free(); }
        }

        private void Close()
        {
            try { _reader.Dispose(); }
            finally { _release(); }
        }
    }
}
