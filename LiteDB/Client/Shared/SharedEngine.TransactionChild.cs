using System;
using System.Collections.Concurrent;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class SharedEngine
    {
        // Parent side: one closed wrapper kept between this connection's handles. It has no core,
        // no native ownership, no coordination participation and no idle file handles; only its
        // settings, mutex objects, owner machinery and reader-registry wrapper survive a handle.
        private SharedEngine _cachedTransactionChild;
        // Child side: the application's ReadTransform for the current handle only, held weakly.
        private HolderReadPolicy _readPolicy;
        // Operation cores whose close reported errors (a failed close checkpoint, a stream dispose).
        // Such a core's wrapper is not reused. Guarded by _useLock.
        private int _closeFailures;
#if DEBUG || TESTING
        internal int TransactionChildrenCreated, TransactionChildrenReused;
        // Why a wrapper was not cached: settings, error, live-state, disposed-parent, occupied.
        internal readonly ConcurrentDictionary<string, int> TransactionChildDiscards = new ConcurrentDictionary<string, int>();
        // Wrappers whose connection was already collected when their handle ended (finalization).
        internal static int TransactionChildrenOrphaned;
        internal SharedEngine CachedTransactionChild { get { lock (_useLock) return _cachedTransactionChild; } }
        // The pooled thread that ran this wrapper's latest handle.
        internal System.Threading.Thread HolderThread;
#endif

        /// <summary>
        /// The wrapper for a new handle's holder: the cached one when it was configured with the same
        /// settings, else a new one. Either starts with this connection's wait recorder and recovery report.
        /// </summary>
        private SharedEngine CheckoutTransactionChild(EngineSettings settings, Func<string, BsonValue, BsonValue> policy)
        {
            SharedEngine child;
            lock (_useLock)
            {
                // Dispose disposes the cache; a begin that was already admitted builds its own wrapper.
                child = _disposed == 0 ? _cachedTransactionChild : null;
                if (child != null) _cachedTransactionChild = null;
            }
            // Rebuild updates this connection's password and collation: never reuse an older configuration.
            if (child != null && !child._settings.SameHolderSettings(settings))
            {
                this.CountChildDiscard("settings");
                child.Dispose();
                child = null;
            }
            if (child == null)
            {
                // The holder must not root application callbacks that can capture the facade or handle;
                // the external resource owner retains the delegate while the handle is live.
                var readPolicy = policy == null ? null : new HolderReadPolicy();
                if (readPolicy != null) settings.ReadTransform = readPolicy.Transform;
                child = new SharedEngine(settings) { _transactionChild = true, _readPolicy = readPolicy };
                child._settings.SharedDurability = _settings.SharedDurability;
                child._settings.CheckpointBackoff = _settings.CheckpointBackoff;
#if DEBUG || TESTING
                this.TransactionChildrenCreated++;
#endif
            }
#if DEBUG || TESTING
            else this.TransactionChildrenReused++;
#endif
            child._readPolicy?.Point(policy);
            // The handle's native wait is this connection's wait: record it here. It also reports
            // and extends this connection's recovery report.
            child._waitRecorder = this.Waits;
            child._recoveryReport = _recoveryReport;
            return child;
        }

        /// <summary>
        /// On the holder thread, after the handle's core closed and native ownership was released:
        /// reset this wrapper to the state of a new one. Returns why it cannot be reused, or null;
        /// the caller disposes a wrapper that cannot.
        /// </summary>
        private string ResetTransactionChild()
        {
            lock (_useLock)
            {
                if (_closeFailures != 0) return "close-failure";
                if (_disposed != 0 || _engine != null || _databaseUsers != 0 || _transactionRunning || _pin != null ||
                    _admittedCalls != 0 || _mutexSnapshots.Count != 0) return "live-state";
            }
            if (_owner.IsHeld) return "live-state";
            // A new wrapper is a one-operation participant: no authority, no demand, no fallback.
            this.DisposeCoordination();
#if NET8_0_OR_GREATER
            _coordinationDemand = 0;
            _coordinationUnavailable = false;
            CoordinationFallbackReason = null;
#endif
            _handles?.CloseIdle();
            _readPolicy?.Clear();
            return _owner.IsHeld ? "live-state" : null;
        }

        /// <summary>Keep a reset wrapper for this connection's next handle; false once disposed or occupied.</summary>
        private bool ReturnTransactionChild(SharedEngine child)
        {
            lock (_useLock)
            {
                if (_disposed == 0 && _cachedTransactionChild == null)
                {
                    _cachedTransactionChild = child;
                    return true;
                }
            }
            this.CountChildDiscard(_disposed != 0 ? "disposed-parent" : "occupied");
            return false;
        }

        private void DisposeCachedTransactionChild()
        {
            SharedEngine cached;
            lock (_useLock)
            {
                cached = _cachedTransactionChild;
                _cachedTransactionChild = null;
            }
            cached?.Dispose();
        }

        private static void CountOrphanedChild()
        {
#if DEBUG || TESTING
            System.Threading.Interlocked.Increment(ref TransactionChildrenOrphaned);
#endif
        }

        private void CountChildDiscard(string reason)
        {
#if DEBUG || TESTING
            this.TransactionChildDiscards.AddOrUpdate(reason, 1, (_, count) => count + 1);
#endif
        }

        /// <summary>
        /// A holder wrapper's ReadTransform: forwards to the current handle's application delegate,
        /// held weakly, so neither a running holder nor an idle cached wrapper roots it.
        /// </summary>
        private sealed class HolderReadPolicy
        {
            private volatile WeakReference<Func<string, BsonValue, BsonValue>> _target;

            internal void Point(Func<string, BsonValue, BsonValue> transform) =>
                _target = transform == null ? null : new WeakReference<Func<string, BsonValue, BsonValue>>(transform);

            internal void Clear() => _target = null;

            internal BsonValue Transform(string collection, BsonValue value)
            {
                var target = _target;
                return target != null && target.TryGetTarget(out var transform)
                    ? transform(collection, value) : throw new ObjectDisposedException("Transaction read policy");
            }
        }
    }
}
