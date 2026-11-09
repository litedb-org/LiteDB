using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LiteDB.Utils;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// This class monitor all open transactions to manage memory usage for each transaction
    /// [Singleton - ThreadSafe]
    /// </summary>
    internal class TransactionMonitor : IDisposable
    {
        private readonly TransactionRegistry _transactions = new TransactionRegistry();
        private readonly ThreadLocal<TransactionService> _slot = new ThreadLocal<TransactionService>();
        private ThreadLocal<bool> _explicitAborted = new ThreadLocal<bool>();

        private readonly HeaderPage _header;
        private readonly LockService _locker;
        private readonly DiskService _disk;
        private readonly WalIndexService _walIndex;

        private readonly int _transactionPageLimit;
        private int _disposed;
        private object _owner = new TransactionOwnerToken();
        internal object Owner => _owner;
        internal void ValidateOwner() => this.ThrowIfDisposed();
        internal TransactionMonitor CreateOwner()
        {
            var view = (TransactionMonitor)this.MemberwiseClone();
            view._owner = new TransactionOwnerToken();
            view._handleOperations = new HandleOperationRegistry();
            view._explicitAborted = new ThreadLocal<bool>();
            return view;
        }

        private HandleOperationRegistry _handleOperations = new HandleOperationRegistry();
        internal void EnterHandleCall(HandleCallLifetime call) => _handleOperations.Enter(call);
        internal void ExitHandleCall(HandleCallLifetime call) => _handleOperations.Exit(call);

        internal void DrainHandleCalls()
        {
            _handleOperations.Refuse();
            var handles = _transactions.Snapshot().Where(t => ReferenceEquals(t.FacadeOwner, _owner) &&
                t.Lease.Kind == TransactionOwnerKind.Handle).ToArray();
            foreach (var transaction in handles) transaction.Lease.Calls.Refuse();
            // An executing writer can wait for an idle owner's collection. Refusal
            // makes idle stay idle; release those owners before draining calls.
            foreach (var transaction in handles.Where(t => !t.Lease.Calls.Running))
            {
                try
                {
                    if (transaction.State == TransactionState.Active) transaction.Rollback();
                    this.RemoveTransaction(transaction, out _);
                }
                finally { _locker.ExitTransaction(transaction.Lease.Admission); }
            }
            _handleOperations.Drain();
        }

        internal void CloseOwner()
        {
            Interlocked.Exchange(ref _disposed, 1);
            _explicitAborted.Dispose();
        }

        internal void DisposeOwner()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var transaction in _transactions.Snapshot())
            {
                if (!ReferenceEquals(transaction.FacadeOwner, _owner)) continue;
                try
                {
                    if (transaction.State == TransactionState.Active)
                    {
                        if (transaction.Lease.Kind == TransactionOwnerKind.Legacy && transaction.OwnerThread != Thread.CurrentThread && transaction.Mode == LockMode.Write)
                            throw new InvalidOperationException("Dispose a Direct facade's active write transaction on its owning thread.");
                        if (transaction.Lease.Kind == TransactionOwnerKind.Handle || transaction.OwnerThread == Thread.CurrentThread) transaction.Rollback();
                    }
                    this.RemoveTransaction(transaction, out _);
                }
                finally
                {
                    if (ReferenceEquals(_slot.Value, transaction)) _slot.Value = null;
                    _locker.ExitTransaction(transaction.Lease.Admission);
                }
            }
            _explicitAborted.Dispose();
        }

#if TESTING
        internal Action BeforeTransactionRegistration;
#endif
#if DEBUG || TESTING
        internal Action AfterTransactionExit { get; set; }
#endif

        // expose open transactions
        public ICollection<TransactionService> Transactions => _transactions.Snapshot();
        public int TransactionPageLimit => _transactionPageLimit;
        public TransactionService[] GetTransactionsSnapshot() => _transactions.Snapshot().ToArray();

        internal bool HasActiveTransaction(object owner)
        {
            return _transactions.Snapshot().Any(transaction =>
                ReferenceEquals(transaction.FacadeOwner, owner) &&
                transaction.State == TransactionState.Active);
        }

        public TransactionMonitor(HeaderPage header, LockService locker, DiskService disk, WalIndexService walIndex, int transactionPageLimit, object owner = null)
        {
            if (transactionPageLimit <= 0) throw new ArgumentOutOfRangeException(nameof(transactionPageLimit));

            _header = header;
            _locker = locker;
            _disk = disk;
            _walIndex = walIndex;
            _transactionPageLimit = transactionPageLimit;
            if (owner != null) _owner = owner;
        }

        // Header writes and host maintenance must check the foreign facade rule
        // before applying their own transaction or multiple-facade restrictions.
        internal void EnsureWriteAllowed()
        {
            this.ThrowIfDisposed();
            var transaction = _slot.Value;
            if (transaction != null && transaction.State != TransactionState.Disposed &&
                (!ReferenceEquals(transaction.FacadeOwner, _owner) || TransactionContext.For(this) != null))
                throw LiteException.SameThreadTransaction();
        }

        public TransactionService GetTransaction(bool create, bool queryOnly, out bool isNew)
        {
            this.ThrowIfDisposed();
            var context = TransactionContext.For(this);
            if (context != null)
            {
                isNew = false;
                var bound = context.Transaction;
                if (bound == null || bound.State != TransactionState.Active)
                    throw new InvalidOperationException("The handle transaction is no longer active.");
                return bound;
            }
            var transaction = _slot.Value;

            if (create && transaction == null)
            {
                isNew = true;

#if TESTING
                if (BeforeTransactionRegistration != null)
                {
                    Reachability.FaultPoint("BeforeTransactionRegistration");
                    BeforeTransactionRegistration();
                }
#endif
                this.ThrowIfDisposed();

                var enteredTransaction = false;
                var owner = TransactionLease.Legacy(_owner);
                try
                {
                    // Checkpoint can reset the WAL ID sequence only while holding
                    // exclusive admission. Take our lease before reserving an ID.
                    _locker.EnterTransaction(owner.Admission);
                    enteredTransaction = true;
                    this.ThrowIfDisposed();
                    transaction = new TransactionService(_header, _locker, _disk, _walIndex, _transactionPageLimit, this, queryOnly, owner);
                    _transactions.Add(transaction);

                    this.ThrowIfDisposed();
                    if (queryOnly == false) _slot.Value = transaction;
                }
                catch
                {
                    if (transaction != null) _transactions.Remove(transaction);
                    try
                    {
                        transaction?.Dispose();
                    }
                    finally
                    {
                        if (enteredTransaction) _locker.ExitTransaction(owner.Admission);
                    }
                    throw;
                }
            }
            else
            {
                isNew = false;
                if (transaction != null && !ReferenceEquals(transaction.FacadeOwner, _owner))
                {
                    if (transaction.State == TransactionState.Disposed)
                    {
                        _slot.Value = null;
                        return this.GetTransaction(create, queryOnly, out isNew);
                    }
                    if (create && !queryOnly)
                    {
                        // Legacy transactions are thread-bound. Phase 4 will add
                        // thread-independent handles; until then, refuse a foreign
                        // same-thread write immediately so it cannot wait on work
                        // that only the owning facade/thread can complete.
                        throw LiteException.SameThreadTransaction();
                    }
                    return null;
                }
            }

            return transaction;
        }

        internal TransactionService CreateTransaction(TransactionLease lease)
        {
            if (lease == null || lease.Kind != TransactionOwnerKind.Handle ||
                !ReferenceEquals(lease.Facade, _owner))
                throw new ArgumentException("Handle lease must belong to this facade.", nameof(lease));
            EnsureWriteAllowed();
            if (_slot.Value != null && _slot.Value.State != TransactionState.Disposed)
                throw LiteException.SameThreadTransaction();
            lease.Claim();
            _locker.EnterTransaction(lease.Admission);
            TransactionService transaction = null;
            try
            {
                ThrowIfDisposed();
                transaction = new TransactionService(_header, _locker, _disk, _walIndex,
                    _transactionPageLimit, this, false, lease);
                _transactions.Add(transaction);
                ThrowIfDisposed();
                return transaction;
            }
            catch
            {
                if (transaction != null) _transactions.Remove(transaction);
                try { transaction?.Dispose(); }
                finally { _locker.ExitTransaction(lease.Admission); }
                throw;
            }
        }

        /// <summary>
        /// Dispose and remove transaction from monitor
        /// without releasing thread lock
        /// </summary>
        private void RemoveTransaction(TransactionService transaction, out bool removed)
        {
            removed = false;
            try
            {
                transaction.Dispose();
            }
            finally
            {
                removed = _transactions.Remove(transaction);
            }
        }

        /// <summary>
        /// Release current thread transaction
        /// </summary>
        public void ReleaseTransaction(TransactionService transaction)
        {
            var removed = false;
            try
            {
                this.RemoveTransaction(transaction, out removed);
            }
            finally
            {
                try
                {
                    // Rebuild may dispose the monitor and disk as soon as the transaction
                    // lease is released. Finish service cleanup before admitting it.
                    if (!transaction.QueryOnly && transaction.Lease.Kind == TransactionOwnerKind.Legacy)
                    {
                        ENSURE(_slot.Value == transaction, "current thread must contains transaction parameter");
                        _slot.Value = null;
                    }
                    _disk.Cache.TrimToLimit();
                }
                finally
                {
                    if (removed)
                    {
                        _locker.ExitTransaction(transaction.Lease.Admission);
#if DEBUG || TESTING
                        AfterTransactionExit?.Invoke();
#endif
                    }
                }
            }
        }

        /// <summary>
        /// Remember that a failed operation rolled back this thread's explicit transaction, so that
        /// the caller's pending Commit is not mistaken for a completion from a foreign thread.
        /// </summary>
        public void MarkExplicitAbort()
        {
            var context = TransactionContext.For(this);
            if (context != null) { context.Aborted = true; return; }
            // Dispose on another thread may already have released the slot; a closing engine has nothing left to complete.
            try
            {
                _explicitAborted.Value = true;
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Read and clear the mark left by <see cref="MarkExplicitAbort"/> on the current thread.
        /// </summary>
        public bool ConsumeExplicitAbort()
        {
            var context = TransactionContext.For(this);
            if (context != null) { var aborted = context.Aborted; context.Aborted = false; return aborted; }
            try
            {
                var aborted = _explicitAborted.Value;
                if (aborted) _explicitAborted.Value = false;
                return aborted;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Get transaction from current thread (from thread slot or from queryOnly) - do not created new transaction
        /// Used only in SystemCollections to get running query transaction
        /// </summary>
        public TransactionService GetThreadTransaction()
        {
            this.ThrowIfDisposed();
            return TransactionContext.For(this)?.Transaction ?? _slot.Value ?? _transactions.FindForThread(Thread.CurrentThread);
        }

        /// <summary>
        /// Check whether a transaction reached its fixed page-retention limit.
        /// </summary>
        public bool CheckSafepoint(TransactionService trans)
        {
            return trans.Pages.TransactionSize >= trans.MaxTransactionSize;
        }

        /// <summary>
        /// Dispose all open transactions
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var cleanup = new LiteDB.Utils.TryCatch();
            foreach (var transaction in _transactions.Close())
            {
                Reachability.Sometimes("maintenance:close-during-active-transaction");
#if DEBUG || TESTING
                if (!ReferenceEquals(transaction.OwnerThread, Thread.CurrentThread)) Reachability.Sometimes("maintenance:close-during-foreign-transaction");
#endif
                cleanup.Catch(transaction.Dispose);
            }

            cleanup.Catch(_slot.Dispose);
            cleanup.Catch(_explicitAborted.Dispose);
            if (cleanup.Exceptions.Count > 0) throw new AggregateException(cleanup.Exceptions);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(TransactionMonitor));
        }
    }
}
