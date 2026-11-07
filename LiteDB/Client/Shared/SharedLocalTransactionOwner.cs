using System;
using System.Threading;
using System.Diagnostics;
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
            return Enter(TransactionLease.Legacy(facade), null);
        }

        internal Lease Enter(TransactionLease identity, TimeSpan? timeout)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            var facade = identity.Facade;
            var thread = Thread.CurrentThread;
            var clock = Stopwatch.StartNew();
            lock (_gate)
            {
                ReleaseDeadOwnerLocked();
                if (_current != null && !ReferenceEquals(_current.Facade, facade) &&
                    _current.Identity.Kind == TransactionOwnerKind.Legacy && ReferenceEquals(_current.Thread, thread))
                {
                    throw LiteException.SameThreadTransaction();
                }

                while (_current != null)
                {
                    // A native holder normally pulses on terminal cleanup. Recheck
                    // actual Thread identity if death precedes its cleanup callback.
                    var remaining = timeout.HasValue ? TransactionWaitBudget.Remaining(timeout.Value) : TimeSpan.FromMilliseconds(100);
                    if (remaining <= TimeSpan.Zero) throw LiteException.LockTimeout("Shared local transaction", timeout.Value);
                    TransactionWaitBudget.Wait(_gate, remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100));
                    ReleaseDeadOwnerLocked();
                    if (_current != null && !ReferenceEquals(_current.Facade, facade) &&
                        _current.Identity.Kind == TransactionOwnerKind.Legacy && ReferenceEquals(_current.Thread, thread))
                    {
                        throw LiteException.SameThreadTransaction();
                    }
                }

                return _current = new Lease(this, identity, ++_generation);
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
                    _current.Identity.Kind == TransactionOwnerKind.Legacy && ReferenceEquals(_current.Thread, thread);
            }
        }

        private void ReleaseDeadOwnerLocked()
        {
            if (_current == null || _current.Identity.Kind == TransactionOwnerKind.Handle || _current.Thread.IsAlive) return;
            _current = null;
            Monitor.PulseAll(_gate);
        }

        internal sealed class Lease : IDisposable
        {
            private SharedLocalTransactionOwner _owner;
            internal readonly TransactionLease Identity;
            internal object Facade => Identity.Facade;
            internal Thread Thread => Identity.Thread;
            internal readonly long Generation;

            internal Lease(SharedLocalTransactionOwner owner, TransactionLease identity, long generation)
            {
                _owner = owner;
                Identity = identity;
                Generation = generation;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.Release(this);
            }
        }
    }
}
