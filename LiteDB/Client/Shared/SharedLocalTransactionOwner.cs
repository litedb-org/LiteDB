using System;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Process-local ownership for the single writable transaction allowed by a
    /// mapped Shared participant. The native mutex still protects the database;
    /// this gate makes same-process ownership explicit before a second facade can
    /// wait on that mutex, and its generation prevents stale cleanup from ending a
    /// later transaction.
    /// </summary>
    internal sealed class SharedLocalTransactionOwner
    {
        private readonly object _gate = new object();
        private Lease _current;
        private long _generation;

        internal readonly TransactionOwnerToken Token = new TransactionOwnerToken();

        internal Lease Enter(object facade)
        {
            var thread = Thread.CurrentThread;
            lock (_gate)
            {
                ReleaseDeadOwnerLocked();
                if (_current != null && !ReferenceEquals(_current.Facade, facade) &&
                    ReferenceEquals(_current.Thread, thread))
                {
                    throw new InvalidOperationException(
                        "Another connection (mapped Shared facade) owns the writable transaction on this thread. " +
                        "Complete it before using this facade on the same thread.");
                }

                while (_current != null)
                {
                    Monitor.Wait(_gate);
                    if (_current != null && !ReferenceEquals(_current.Facade, facade) &&
                        ReferenceEquals(_current.Thread, thread))
                    {
                        throw new InvalidOperationException(
                            "Another connection (mapped Shared facade) owns the writable transaction on this thread. " +
                            "Complete it before using this facade on the same thread.");
                    }
                }

                return _current = new Lease(this, facade, thread, ++_generation);
            }
        }

        internal void Release(Lease lease)
        {
            if (lease == null) return;
            lock (_gate)
            {
                if (!ReferenceEquals(_current, lease) || lease.Generation != _generation) return;
                _current = null;
                Monitor.PulseAll(_gate);
            }
        }

        internal bool IsHeldByOther(object facade, Thread thread)
        {
            lock (_gate)
            {
                ReleaseDeadOwnerLocked();
                return _current != null && !ReferenceEquals(_current.Facade, facade) &&
                    ReferenceEquals(_current.Thread, thread);
            }
        }

        private void ReleaseDeadOwnerLocked()
        {
            if (_current == null || _current.Thread.IsAlive) return;
            _current = null;
            Monitor.PulseAll(_gate);
        }

        internal sealed class Lease : IDisposable
        {
            private SharedLocalTransactionOwner _owner;
            internal readonly object Facade;
            internal readonly Thread Thread;
            internal readonly long Generation;

            internal Lease(SharedLocalTransactionOwner owner, object facade, Thread thread, long generation)
            {
                _owner = owner;
                Facade = facade;
                Thread = thread;
                Generation = generation;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.Release(this);
            }
        }
    }
}
