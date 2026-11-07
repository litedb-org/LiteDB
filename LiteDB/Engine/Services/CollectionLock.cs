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
        private readonly LinkedList<object> _waiters = new LinkedList<object>();
#if DEBUG || TESTING
        internal Action BeforeWait;
#endif

        /// <param name="owner">The owning thread or <see cref="object"/> handle.</param>
        /// <param name="timeout">How long to wait for another owner.</param>
        public bool TryEnter(object owner, TimeSpan timeout)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            var started = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                if (ReferenceEquals(_owner, owner)) { _depth++; return true; }
                var request = _waiters.AddLast(owner);
                try
                {
                    while (_owner != null || !ReferenceEquals(_waiters.First, request))
                    {
                        if (TransactionContext.IsExecutingOwner(_owner)) throw new TransactionWaitRefusedException(owner);
                        if (ReferenceEquals(_owner, Thread.CurrentThread)) return false;
                        var remaining = timeout - TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
                        remaining = Min(remaining, TransactionWaitBudget.Remaining(timeout));
                        if (remaining <= TimeSpan.Zero) return false;
#if DEBUG || TESTING
                        BeforeWait?.Invoke();
#endif
                        TransactionWaitBudget.Wait(_lock, remaining);
                    }
                    _owner = owner;
                    _depth++;
                    return true;
                }
                finally
                {
                    _waiters.Remove(request);
                    Monitor.PulseAll(_lock);
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
                Monitor.PulseAll(_lock);
            }
        }
    }
}
