using System;
using System.Diagnostics;
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
#if DEBUG || TESTING
        internal Action BeforeWait;
#endif

        /// <param name="owner">The owning thread or <see cref="TransactionContext"/> handle.</param>
        /// <param name="timeout">How long to wait for another owner.</param>
        public bool TryEnter(object owner, TimeSpan timeout)
        {
            var started = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                while (_owner != null && !ReferenceEquals(_owner, owner))
                {
                    // The other owner can make progress only after this thread returns: a legacy
                    // transaction of this thread, or a handle executing this callback.
                    if (ReferenceEquals(_owner, Thread.CurrentThread) ||
                        ReferenceEquals((_owner as TransactionContext)?.ExecutingThread, Thread.CurrentThread)) return false;
                    var elapsed = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
                    var remaining = timeout - TimeSpan.FromSeconds(elapsed);
                    if (remaining <= TimeSpan.Zero) return false;
#if DEBUG || TESTING
                    BeforeWait?.Invoke();
#endif
                    if (!Monitor.Wait(_lock, remaining)) return false;
                }
                _owner = owner;
                _depth++;
                return true;
            }
        }

        public void Exit(object owner)
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_owner, owner)) throw new SynchronizationLockException("Collection lock belongs to another transaction.");
                if (--_depth != 0) return;
                _owner = null;
                Monitor.PulseAll(_lock);
            }
        }
    }
}
