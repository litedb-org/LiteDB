using System;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB
{
    public partial class SharedEngine
    {
        #region Transaction Operations

        public bool BeginTrans() => this.Call(this.BeginTransCore);

        private bool BeginTransCore()
        {
            this.ThrowIfCallerRetainsOwnership();
            // A pending admission belongs to this call, not to the old native
            // ownership. Dead-owner cleanup must never release the successor's lease.
            SharedLocalTransactionOwner.Lease pending = null;
            if (_localTransactions != null &&
                !ReferenceEquals(_localTransaction?.Thread, Thread.CurrentThread))
                pending = _localTransactions.Enter(this);

            var use = default(SharedMutexPin);
            var opened = false;

            try
            {
                use = OpenDatabase();
                opened = true;
                var started = _engine.BeginTrans();
                if (started)
                {
                    // A new inner transaction proves the old one aborted. Return its hold
                    // while this call still protects the engine and the new transaction.
                    if (_transactionRunning) CloseDatabase(_transactionUse, hold: true);
                    if (pending != null)
                    {
                        _localTransaction = pending;
                        pending = null;
                    }
                    _transactionThread = Thread.CurrentThread;
                    _transactionRunning = true;
                    // A pinned transaction keeps the pin until it completes.
                    _transactionUse = use;
                    use?.ToHold();
                }
                // A false join belongs to the surrounding explicit or automatic
                // transaction; its caller owes no completion or mutex recursion.
                else
                {
                    CloseDatabase(use);
                }
                return started;
            }
            catch
            {
                if (opened)
                {
                    try { this.ReconcileMappedTransaction(); }
                    finally { CloseDatabase(use); }
                }
                throw;
            }
            finally { pending?.Dispose(); }
        }

        public bool Commit() => this.Call(() => CompleteTransaction(commit: true));

        public bool Rollback() => this.Call(() => CompleteTransaction(commit: false));

        private bool CompleteTransaction(bool commit)
        {
            this.ThrowIfForeignLocalTransaction();
            // Hold one extra mutex recursion (or pinned operation) throughout
            // completion. A foreign thread must not reach cleanup, even while
            // BeginTrans is publishing.
            var pin = _pin;
            var pinned = pin != null && pin.TryEnter();
            if (!pinned && !_owner.TryEnter(out _))
            {
                // Rolling back nothing is safe and must not replace the error a catch block is handling.
                if (!_transactionRunning || !commit) return false;
                Reachability.Sometimes("refusal:shared-commit-foreign-thread");
                throw ForeignTransactionCompletion();
            }

            try
            {
                // Dispose ended the transaction with the connection; a rollback in a catch
                // block must not replace the error it handles.
                lock (_useLock)
                {
                    if (!commit && Volatile.Read(ref _disposed) != 0) return false;
                    this.AdmitLocked();
                }
                RejectAbandonedTransaction();
                if (!_transactionRunning || _engine == null) return false;
                try
                {
                    this.AtOwnershipStage(commit ? "before-commit" : "before-rollback");
                    return commit ? _engine.Commit() : _engine.Rollback();
                }
                finally
                {
                    this.EndTransaction(releaseHold: true);
                }
            }
            finally
            {
                if (pinned) pin.Exit(hold: false);
                else _owner.Exit();
            }
        }

        private void RejectAbandonedTransaction()
        {
            // Called only while owning the named mutex. A live explicit owner
            // retains a recursion, so acquisition on another thread proves that
            // ownership was abandoned, even if another instance consumed the signal.
            if (!_transactionRunning || ReferenceEquals(_transactionThread, Thread.CurrentThread)) return;
            this.EndTransaction(releaseHold: false);
            _databaseUsers = 0;
            var orphan = _engine;
            _engine = null;
            // The mutex was free since the owner exited, so another process may have
            // committed or checkpointed. This engine's WAL index and cache can be stale:
            // release it without the close checkpoint; the next open recovers the WAL.
            if (orphan != null) this.CloseRetainedCore(orphan, checkpoint: false);
            _handles?.CloseIdle();
            Reachability.Sometimes("refusal:shared-abandoned-transaction");
            throw new LiteException(0, "The explicit transaction owner thread exited. Its uncommitted work was discarded; begin a new transaction on one thread.");
        }

        private static LiteException ForeignTransactionCompletion() =>
            new LiteException(0, "Complete the explicit transaction on the same thread that called BeginTrans; do not await inside it.");

        private void ThrowIfForeignLocalTransaction()
        {
            if (_localTransactions?.IsHeldByOther(this, Thread.CurrentThread) == true)
                throw new InvalidOperationException(
                    "Another connection (mapped Shared facade) owns the writable transaction on this thread. " +
                    "Complete it before using this facade on the same thread.");
        }

        /// <summary>
        /// The terminal transaction transition. The caller owns native main (or
        /// its pin), so no successor can observe half-detached facade state. Capture
        /// each resource before clearing it; stale result cleanup cannot release a
        /// successor's generation. Abandon/dispose already drain native holds.
        /// </summary>
        private void EndTransaction(bool releaseHold)
        {
            var use = _transactionUse;
            var local = Interlocked.Exchange(ref _localTransaction, null);
            var running = _transactionRunning;
            _transactionUse = null;
            _transactionRunning = false;
            _transactionThread = null;
            try
            {
                if (releaseHold && running) this.CloseDatabase(use, hold: true);
            }
            finally { local?.Dispose(); }
        }

        // Operations can implicitly abort or Stop the core. Reconcile before the
        // operation returns, while its extra native use still protects teardown.
        private void ReconcileMappedTransaction()
        {
            if (_localTransactions == null || !_transactionRunning || _engine == null ||
                (!_engine.StorageUncertain && _engine.HasActiveTransaction)) return;
            this.EndTransaction(releaseHold: true);
        }

        #endregion
    }
}
