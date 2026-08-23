using System;
using System.Threading;

namespace LiteDB.Engine
{
    /// <summary>
    /// Provides a target-specific collection lock while preserving timed Monitor semantics.
    /// </summary>
    internal sealed class CollectionLock
    {
#if NET9_0_OR_GREATER
        private readonly Lock _lock = new Lock();
#else
        private readonly object _lock = new object();
#endif

        public bool TryEnter(TimeSpan timeout)
        {
#if NET9_0_OR_GREATER
            return _lock.TryEnter(timeout);
#else
            return Monitor.TryEnter(_lock, timeout);
#endif
        }

        public void Exit()
        {
#if NET9_0_OR_GREATER
            _lock.Exit();
#else
            Monitor.Exit(_lock);
#endif
        }
    }
}
