using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace LiteDB.Engine
{
    /// <summary>
    /// Transaction read leases can be released by a cursor-disposal thread.
    /// A lease belongs to its thread, or to an explicit transaction handle that may run on
    /// any thread. Exclusive operations remain owned by the thread performing the checkpoint.
    /// </summary>
    internal sealed class TransactionGate : IDisposable
    {
        private readonly object _sync = new object();
        private readonly Func<object> _owner;
        // A cursor can outlive its originating thread. Numeric managed IDs can
        // be recycled after that thread is collected, so retain its identity
        // until the last lease is released, including release on another thread.
        private readonly Dictionary<object, int> _readers = new Dictionary<object, int>();
        private int _readerCount;
        private Thread _writer;
        private int _waitingWriters;
        private bool _disposed;

        internal TransactionGate(Func<object> owner = null) { _owner = owner; }

        private object CurrentOwner => _owner?.Invoke() ?? Thread.CurrentThread;

        public bool IsReadLockHeld
        {
            get { var owner = CurrentOwner; lock (_sync) return _readers.ContainsKey(owner); }
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
                var owner = CurrentOwner;
                lock (_sync) return _readers.TryGetValue(owner, out var count) ? count : 0;
            }
        }

        public bool TryEnterReadLock(TimeSpan timeout) => TryEnterReadLock(timeout, CurrentOwner);

        internal bool TryEnterReadLock(TimeSpan timeout, object owner)
        {
            var elapsed = Stopwatch.StartNew();
            var thread = owner;
            lock (_sync)
            {
                ThrowIfDisposed();
                // Writer priority must not queue a callback behind a writer that is itself
                // waiting for the handle executing that callback on this thread, nor a handle
                // begun on a thread whose own cursor lease that writer waits for.
                while (_writer != null || (_waitingWriters != 0 && !_readers.ContainsKey(thread) &&
                    !_readers.ContainsKey(Thread.CurrentThread) && !HeldByExecutingHandle(Thread.CurrentThread)))
                {
                    if (!Wait(timeout, elapsed)) return false;
                }
                _readers.TryGetValue(thread, out var count);
                _readers[thread] = count + 1;
                _readerCount++;
                return true;
            }
        }

        public void ExitReadLock(object owner)
        {
            lock (_sync)
            {
                // Transactions created within an exclusive operation do not take
                // separate leases. Disposal after engine shutdown is also harmless.
                if (ReferenceEquals(_writer, owner) || !_readers.TryGetValue(owner, out var count)) return;
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
            var current = CurrentOwner;
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_readers.ContainsKey(current)) throw new LockRecursionException("Cannot enter exclusive mode inside a transaction.");
                _waitingWriters++;
                try
                {
                    while (_writer != null || _readerCount != 0)
                    {
                        // A handle executing this callback keeps its lease until the
                        // callback returns: waiting for it cannot make progress.
                        if (HeldByExecutingHandle(thread)) return false;
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

        private bool HeldByExecutingHandle(Thread thread)
        {
            foreach (var reader in _readers.Keys)
                if (reader is TransactionContext handle && ReferenceEquals(handle.ExecutingThread, thread)) return true;
            return false;
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
