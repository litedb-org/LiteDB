using System;
using System.Threading;
#if DEBUG || TESTING
using LiteDB.Utils;
#endif

namespace LiteDB.Engine
{
    /// <summary>
    /// Provides a target-specific collection lock while preserving timed Monitor semantics.
    /// </summary>
    internal sealed class CollectionLock
    {
#if NET9_0_OR_GREATER
        private readonly Lock _lock = new Lock();
#if DEBUG || TESTING
        /// <summary>Wait-for graph resource; the lock service names it after its collection.</summary>
        internal readonly WaitGraph.Resource Graph = new WaitGraph.Resource("collection-lock", null, WaitPrimitive.Lock);
#endif
#else
        private readonly object _lock = new object();
#if DEBUG || TESTING
        /// <summary>Wait-for graph resource; the lock service names it after its collection.</summary>
        internal readonly WaitGraph.Resource Graph = new WaitGraph.Resource("collection-lock", null, WaitPrimitive.Monitor);
#endif
#endif

        public bool TryEnter(TimeSpan timeout)
        {
#if DEBUG || TESTING
            // Register a wait only when the lock is contended; a free or recursive entry never blocks.
            if (!WaitGraph.Enabled) return this.TryEnterCore(timeout);
            if (!this.TryEnterCore(TimeSpan.Zero))
            {
                using (WaitGraph.Wait(this.Graph, WaitBound.After(timeout), "CollectionLock.TryEnter"))
                    if (!this.TryEnterCore(timeout)) return false;
            }
            WaitGraph.Acquired(this.Graph, site: "CollectionLock.TryEnter");
            return true;
        }

        private bool TryEnterCore(TimeSpan timeout)
        {
#endif
#if NET9_0_OR_GREATER
            return _lock.TryEnter(timeout);
#else
            return Monitor.TryEnter(_lock, timeout);
#endif
        }

        public void Exit()
        {
#if DEBUG || TESTING
            WaitGraph.Released(this.Graph);
#endif
#if NET9_0_OR_GREATER
            _lock.Exit();
#else
            Monitor.Exit(_lock);
#endif
        }
    }
}
