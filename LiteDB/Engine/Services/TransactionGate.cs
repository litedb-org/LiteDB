using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using LiteDB.Utils;

namespace LiteDB.Engine
{
    /// <summary>
    /// Transaction read leases can be released by a cursor-disposal thread.
    /// Exclusive operations remain owned by the thread performing the checkpoint.
    /// </summary>
    internal sealed class TransactionGate : IDisposable
    {
        private readonly object _sync = new object();
        // A cursor can outlive its originating thread. Numeric managed IDs can
        // be recycled after that thread is collected, so retain its identity
        // until the last lease is released, including release on another thread.
        private readonly Dictionary<Thread, int> _readers = new Dictionary<Thread, int>();
        private int _readerCount;
        private Thread _writer;
        private int _waitingWriters;
        private bool _disposed;
#if DEBUG || TESTING
        // Wait-for graph: leases (readers and the writer) block a writer; writers (active or
        // queued) block a new reader.
        private readonly WaitGraph.Resource _graphLeases = new WaitGraph.Resource("transaction-gate", "leases", WaitPrimitive.Gate);
        private readonly WaitGraph.Resource _graphWriters = new WaitGraph.Resource("transaction-gate", "writers", WaitPrimitive.Gate, ordered: false);
#endif

        public bool IsReadLockHeld
        {
            get { lock (_sync) return _readers.ContainsKey(Thread.CurrentThread); }
        }

        public bool IsWriteLockHeld
        {
            get { lock (_sync) return _writer == Thread.CurrentThread; }
        }

        public int CurrentReadCount
        {
            get { lock (_sync) return _readerCount; }
        }

        public int RecursiveReadCount
        {
            get
            {
                lock (_sync) return _readers.TryGetValue(Thread.CurrentThread, out var count) ? count : 0;
            }
        }

        public bool TryEnterReadLock(TimeSpan timeout)
        {
            var elapsed = Stopwatch.StartNew();
            var thread = Thread.CurrentThread;
            lock (_sync)
            {
                ThrowIfDisposed();
                while (_writer != null || (_waitingWriters != 0 && !_readers.ContainsKey(thread)))
                {
#if DEBUG || TESTING
                    using (WaitGraph.Wait(_graphWriters, WaitBound.After(timeout), "TransactionGate.TryEnterReadLock"))
#endif
                    if (!Wait(timeout, elapsed)) return false;
                }
                _readers.TryGetValue(thread, out var count);
                _readers[thread] = count + 1;
                _readerCount++;
#if DEBUG || TESTING
                WaitGraph.Acquired(_graphLeases, site: "TransactionGate.TryEnterReadLock");
#endif
                return true;
            }
        }

        public void ExitReadLock(Thread owner)
        {
            lock (_sync)
            {
                // Transactions created within an exclusive operation do not take
                // separate leases. Disposal after engine shutdown is also harmless.
                if (_writer == owner || !_readers.TryGetValue(owner, out var count)) return;
#if DEBUG || TESTING
                WaitGraph.Released(_graphLeases, owner);
#endif
                if (count == 1) _readers.Remove(owner);
                else _readers[owner] = count - 1;
                _readerCount--;
                Monitor.PulseAll(_sync);
            }
        }

        public bool TryEnterWriteLock(TimeSpan timeout)
        {
            var elapsed = Stopwatch.StartNew();
            var thread = Thread.CurrentThread;
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_readers.ContainsKey(thread))
                {
                    Reachability.Sometimes("refusal:reader-upgrade-to-exclusive");
                    throw new LockRecursionException("Cannot enter exclusive mode inside a transaction.");
                }
                _waitingWriters++;
#if DEBUG || TESTING
                WaitGraph.Acquired(_graphWriters, site: "TransactionGate.TryEnterWriteLock (queued)");
#endif
                try
                {
                    while (_writer != null || _readerCount != 0)
                    {
#if DEBUG || TESTING
                        using (WaitGraph.Wait(_graphLeases, WaitBound.After(timeout), "TransactionGate.TryEnterWriteLock"))
#endif
                        if (!Wait(timeout, elapsed)) return false;
                    }
                    _writer = thread;
#if DEBUG || TESTING
                    WaitGraph.Acquired(_graphLeases, site: "TransactionGate.TryEnterWriteLock");
                    WaitGraph.Acquired(_graphWriters, site: "TransactionGate.TryEnterWriteLock");
#endif
                    return true;
                }
                finally
                {
#if DEBUG || TESTING
                    WaitGraph.Released(_graphWriters);
#endif
                    _waitingWriters--;
                    Monitor.PulseAll(_sync);
                }
            }
        }

        public bool TryEnterWriteLock(int milliseconds) => TryEnterWriteLock(TimeSpan.FromMilliseconds(milliseconds));

        public void ExitWriteLock()
        {
            lock (_sync)
            {
                if (_writer != Thread.CurrentThread) throw new SynchronizationLockException();
#if DEBUG || TESTING
                WaitGraph.Released(_graphLeases);
                WaitGraph.Released(_graphWriters);
#endif
                _writer = null;
                Monitor.PulseAll(_sync);
            }
        }

        private bool Wait(TimeSpan timeout, Stopwatch elapsed)
        {
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) return false;
            Monitor.Wait(_sync, remaining);
            ThrowIfDisposed();
            return true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TransactionGate));
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
#if DEBUG || TESTING
                WaitGraph.ReleaseAll(_graphLeases);
                WaitGraph.ReleaseAll(_graphWriters);
#endif
                _readers.Clear();
                _readerCount = 0;
                Monitor.PulseAll(_sync);
            }
        }
    }
}
