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
            var acquiredLocal = false;
            if (_localTransactions != null && !_transactionRunning)
            {
                _localTransaction = _localTransactions.Enter(this);
                acquiredLocal = true;
            }

            var use = default(SharedMutexPin);

            try
            {
                use = OpenDatabase();
                var started = _engine.BeginTrans();
                if (started)
                {
                    // A new inner transaction proves the old one aborted. Return its hold
                    // while this call still protects the engine and the new transaction.
                    if (_transactionRunning) CloseDatabase(_transactionUse, hold: true);
                    _transactionThreadId = Environment.CurrentManagedThreadId;
                    _transactionRunning = true;
                    // A pinned transaction keeps the pin until it completes.
                    _transactionUse = use;
                    use?.ToHold();
                }
                // A false join belongs to the surrounding explicit or automatic
                // transaction; its caller owes no completion or mutex recursion.
                else
                {
                    if (acquiredLocal) this.ReleaseLocalTransaction();
                    CloseDatabase(use);
                }
                return started;
            }
            catch
            {
                if (acquiredLocal) this.ReleaseLocalTransaction();
                this.ReconcileMappedTransaction();
                CloseDatabase(use);
                throw;
            }
        }

        public bool Commit() => this.Call(() => CompleteTransaction(commit: true));

        public bool Rollback() => this.Call(() => CompleteTransaction(commit: false));

        private bool CompleteTransaction(bool commit)
        {
            if (_localTransactions?.IsHeldByOther(this, Thread.CurrentThread) == true)
                throw new InvalidOperationException(
                    "Another connection (mapped Shared facade) owns the writable transaction on this thread. " +
                    "Complete it before using this facade on the same thread.");
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
                try { return commit ? _engine.Commit() : _engine.Rollback(); }
                finally
                {
                    var use = _transactionUse;
                    _transactionUse = null;
                    _transactionRunning = false;
                    _transactionThreadId = 0;
                    this.ReleaseLocalTransaction();
                    CloseDatabase(use, hold: true);
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
            if (!_transactionRunning || _transactionThreadId == Environment.CurrentManagedThreadId) return;
            _transactionRunning = false;
            _transactionThreadId = 0;
            this.ReleaseLocalTransaction();
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

        private void ReleaseLocalTransaction()
        {
            var lease = Interlocked.Exchange(ref _localTransaction, null);
            lease?.Dispose();
        }

        /// <summary>
        /// A failed transaction retry can discover that the engine already ended
        /// its transaction. Reconcile that terminal state so stale native
        /// recursion and participant-local ownership cannot survive the retry.
        /// </summary>
        private void ReconcileMappedTransaction()
        {
            if (_localTransactions == null || !_transactionRunning || _engine == null || _engine.HasActiveTransaction) return;
            var use = _transactionUse;
            _transactionUse = null;
            _transactionRunning = false;
            _transactionThreadId = 0;
            this.ReleaseLocalTransaction();
            this.CloseDatabase(use, hold: true);
        }

        #endregion
    }
}
