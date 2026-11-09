using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;

namespace LiteDB.Engine
{
    /// <summary>
    /// A collection writer lock owned by an explicit object instead of the executing thread.
    /// Legacy, automatic and cursor transactions own it through their thread, recursively as
    /// before; an explicit transaction handle owns it itself, so a later call of that handle on
    /// another thread can continue and release it, while another handle on the same thread waits.
    /// </summary>
    internal sealed class CollectionLock
    {
        private readonly object _lock = new object();
        private object _owner;
        private int _depth;
        private readonly LinkedList<ManualResetEventSlim> _waiters = new LinkedList<ManualResetEventSlim>();
#if DEBUG || TESTING
        internal Action BeforeWait;
#endif

        /// <param name="owner">The owning thread or <see cref="object"/> handle.</param>
        /// <param name="timeout">How long to wait for another owner.</param>
        public bool TryEnter(object owner, TimeSpan timeout)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            var started = Stopwatch.GetTimestamp();
            LinkedListNode<ManualResetEventSlim> request;
            lock (_lock)
            {
                if (ReferenceEquals(_owner, owner)) { _depth++; return true; }
                if (_owner == null && _waiters.Count == 0)
                {
                    _owner = owner;
                    _depth = 1;
                    return true;
                }
                // Only the FIFO head spins briefly for a release; other requests park.
                request = _waiters.AddLast(new ManualResetEventSlim(false, _waiters.Count == 0 ? 10 : 0));
            }
            try
            {
                while (true)
                {
                    TimeSpan remaining;
                    lock (_lock)
                    {
                        if (_owner == null && ReferenceEquals(_waiters.First, request))
                        {
                            _owner = owner;
                            _depth = 1;
                            return true;
                        }
                        if (TransactionContext.IsExecutingOwner(_owner)) throw new TransactionWaitRefusedException(owner);
                        if (ReferenceEquals(_owner, Thread.CurrentThread)) return false;
                        remaining = timeout - TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
                        remaining = Min(remaining, TransactionWaitBudget.Remaining(timeout));
                        if (remaining <= TimeSpan.Zero) return false;
#if DEBUG || TESTING
                        BeforeWait?.Invoke();
#endif
                    }
                    // Set is persistent, so a release between the check and Wait cannot be lost.
                    TransactionWaitBudget.Wait(request.Value, remaining);
                }
            }
            finally
            {
                lock (_lock)
                {
                    var wasHead = ReferenceEquals(_waiters.First, request);
                    _waiters.Remove(request);
                    request.Value.Dispose();
                    if (wasHead && _owner == null) _waiters.First?.Value.Set();
                }
            }
        }

        private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

        public void Exit(object owner)
        {
            lock (_lock)
            {
                if (owner == null || !ReferenceEquals(_owner, owner)) throw new SynchronizationLockException("Collection lock belongs to another transaction.");
                if (--_depth != 0) return;
                _owner = null;
                _waiters.First?.Value.Set();
            }
        }
    }
}
