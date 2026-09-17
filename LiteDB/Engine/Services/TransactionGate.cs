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
        private readonly Dictionary<int, int> _readers = new Dictionary<int, int>();
        private int _readerCount;
        private int _writer;
        private int _waitingWriters;
        private bool _disposed;

        public bool IsReadLockHeld
        {
            get { lock (_sync) return _readers.ContainsKey(Environment.CurrentManagedThreadId); }
        }

        public bool IsWriteLockHeld
        {
            get { lock (_sync) return _writer == Environment.CurrentManagedThreadId; }
        }

        public int CurrentReadCount
        {
            get { lock (_sync) return _readerCount; }
        }

        public int RecursiveReadCount
        {
            get
            {
                lock (_sync) return _readers.TryGetValue(Environment.CurrentManagedThreadId, out var count) ? count : 0;
            }
        }

        public bool TryEnterReadLock(TimeSpan timeout)
        {
            var elapsed = Stopwatch.StartNew();
            var thread = Environment.CurrentManagedThreadId;
            lock (_sync)
            {
                ThrowIfDisposed();
                while (_writer != 0 || (_waitingWriters != 0 && !_readers.ContainsKey(thread)))
                {
                    if (!Wait(timeout, elapsed)) return false;
                }
                _readers.TryGetValue(thread, out var count);
                _readers[thread] = count + 1;
                _readerCount++;
                return true;
            }
        }

        public void ExitReadLock(int owner)
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
            var thread = Environment.CurrentManagedThreadId;
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_readers.ContainsKey(thread)) throw new LockRecursionException("Cannot enter exclusive mode inside a transaction.");
                _waitingWriters++;
                try
                {
                    while (_writer != 0 || _readerCount != 0)
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
                if (_writer != Environment.CurrentManagedThreadId) throw new SynchronizationLockException();
                _writer = 0;
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
