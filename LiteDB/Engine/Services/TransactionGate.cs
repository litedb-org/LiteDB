using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

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
                    if (!Wait(timeout, elapsed)) return false;
                }
                _readers.TryGetValue(thread, out var count);
                _readers[thread] = count + 1;
                _readerCount++;
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
                if (_readers.ContainsKey(thread)) throw new LockRecursionException("Cannot enter exclusive mode inside a transaction.");
                _waitingWriters++;
                try
                {
                    while (_writer != null || _readerCount != 0)
                    {
                        if (!Wait(timeout, elapsed)) return false;
                    }
                    _writer = thread;
                    return true;
                }
                finally
                {
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
                _readers.Clear();
                _readerCount = 0;
                Monitor.PulseAll(_sync);
            }
        }
    }
}
