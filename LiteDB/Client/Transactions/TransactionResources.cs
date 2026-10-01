using System;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB
{
    /// <summary>A transaction's storage dependency, separate from its execution guard.</summary>
    internal sealed class TransactionResources : IDisposable
    {
        internal readonly LiteEngine Engine;
        internal readonly string SharedMutexName;
        private Action _release;
        private Action _abandon;
        private object _policyAnchor;

        /// <param name="engine">The storage engine that executes the handle's operations.</param>
        /// <param name="release">Releases storage ownership after completion (once).</param>
        /// <param name="abandon">Releases ownership without I/O when the handle is collected undisposed.</param>
        /// <param name="policyAnchor">Keeps weakly referenced application policy alive while the handle is.</param>
        /// <param name="sharedMutexName">The Shared writer namespace the handle owns, if any.</param>
        internal TransactionResources(LiteEngine engine, Action release, Action abandon = null, object policyAnchor = null, string sharedMutexName = null)
        {
            Engine = engine;
            SharedMutexName = sharedMutexName;
            _release = release;
            _abandon = abandon;
            _policyAnchor = policyAnchor;
        }

        public void Dispose()
        {
            _abandon = null;
            _policyAnchor = null;
            GC.SuppressFinalize(this);
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }

        ~TransactionResources() { try { _abandon?.Invoke(); } catch { } }
    }
}
