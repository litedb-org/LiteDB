using System;
using System.Runtime.InteropServices;
using System.Threading;
using LiteDB.Utils;

namespace LiteDB.Client.Shared
{
    // Only callback-free page/lease state is rooted. The wrapper owns the cursor
    // graph until its finalizer publishes this record; user callbacks in that
    // graph may point back to the wrapper without making it a permanent GC root.
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
#if TESTING
        internal Func<WaitCallback, object, bool> DispatchOverride;
#endif

        private static Timer CreateReaper()
        {
            // A process-wide reaper must not retain its first caller's ambient
            // ExecutionContext (which can itself contain a facade or request).
            if (ExecutionContext.IsFlowSuppressed()) return new Timer(_ => Drain(), null, 20, 20);
            using (ExecutionContext.SuppressFlow()) return new Timer(_ => Drain(), null, 20, 20);
        }

        internal SharedReaderCleanup(IBsonDataReader reader, Action release,
            string ns = null, object connection = null, Func<bool> retains = null, System.IO.FileStream[] leaseFiles = null)
        {
            _reader = reader;
            _release = release;
            _namespace = ns;
            _connection = connection;
            _retains = retains;
            _root = GCHandle.Alloc(new object[] { SharedReaderResources.Preserve(reader), leaseFiles });
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
                pending._next = null;
                // A release can block on a connection gate or file I/O. Dispatch
                // every owner independently so it cannot strand the rest of this
                // batch. Do not flow a draining caller's ambient context either.
                if (!TryDispatch(pending))
                {
                    // Keep the resource root and retry on the existing periodic timer.
                    // Framework can return false after queueing; Dispose's atomic
                    // guard makes a later duplicate callback harmless.
                    pending.Abandon();
                }
                pending = next;
            }
        }

        private static bool TryDispatch(SharedReaderCleanup owner)
        {
            try
            {
#if TESTING
                Reachability.FaultPoint("DispatchOverride");
                var dispatch = owner.DispatchOverride;
                if (dispatch != null) return dispatch(Reap, owner);
#endif
                return ThreadPool.UnsafeQueueUserWorkItem(Reap, owner);
            }
            catch (Exception) { return false; }
        }

        private static void Reap(object state)
        {
            try { ((SharedReaderCleanup)state).Dispose(); }
            catch (Exception) { /* Abandoned cleanup cannot escape the worker. */ }
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
