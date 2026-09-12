using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

        private readonly HeaderPage _header;
        private readonly LockService _locker;
        private readonly DiskService _disk;
        private readonly WalIndexService _walIndex;

        private readonly int _transactionPageLimit;
        private int _disposed;

#if TESTING
        internal Action BeforeTransactionRegistration { get; set; }
#endif

        // expose open transactions
        public ICollection<TransactionService> Transactions => _transactions.Snapshot();
        public int TransactionPageLimit => _transactionPageLimit;
        public TransactionService[] GetTransactionsSnapshot() => _transactions.Snapshot().ToArray();

        public TransactionMonitor(HeaderPage header, LockService locker, DiskService disk, WalIndexService walIndex, int transactionPageLimit)
        {
            if (transactionPageLimit <= 0) throw new ArgumentOutOfRangeException(nameof(transactionPageLimit));

            _header = header;
            _locker = locker;
            _disk = disk;
            _walIndex = walIndex;
            _transactionPageLimit = transactionPageLimit;
        }

        public TransactionService GetTransaction(bool create, bool queryOnly, out bool isNew)
        {
            this.ThrowIfDisposed();
            var transaction = _slot.Value;

            if (create && transaction == null)
            {
                isNew = true;

#if TESTING
                BeforeTransactionRegistration?.Invoke();
#endif
                this.ThrowIfDisposed();

                var alreadyLock = _transactions.FindForThread(Environment.CurrentManagedThreadId) != null;
                transaction = new TransactionService(_header, _locker, _disk, _walIndex, _transactionPageLimit, this, queryOnly);
                var enteredTransaction = false;
                try
                {
                    _transactions.Add(transaction);
                    if (alreadyLock == false)
                    {
                        _locker.EnterTransaction();
                        enteredTransaction = true;
                    }

                    this.ThrowIfDisposed();
                    if (queryOnly == false) _slot.Value = transaction;
                }
                catch
                {
                    _transactions.Remove(transaction);
                    try
                    {
                        transaction.Dispose();
                    }
                    finally
                    {
                        if (enteredTransaction) _locker.ExitTransaction();
                    }
                    throw;
                }
            }
            else
            {
                isNew = false;
            }

            return transaction;
        }

        /// <summary>
        /// Dispose and remove transaction from monitor
        /// without releasing thread lock
        /// </summary>
        private void RemoveTransaction(TransactionService transaction)
        {
            try
            {
                transaction.Dispose();
            }
            finally
            {
                _transactions.Remove(transaction);
            }
        }

        /// <summary>
        /// Release current thread transaction
        /// </summary>
        public void ReleaseTransaction(TransactionService transaction)
        {
            try
            {
                this.RemoveTransaction(transaction);
            }
            finally
            {
                // Removal must precede this check, including when disposal fails.
                if (_transactions.FindForThread(Environment.CurrentManagedThreadId) == null)
                {
                    _locker.ExitTransaction();
                }
                if (!transaction.QueryOnly)
                {
                    ENSURE(_slot.Value == transaction, "current thread must contains transaction parameter");
                    _slot.Value = null;
                }
                _disk.Cache.TrimToLimit();
            }
        }

        /// <summary>
        /// Get transaction from current thread (from thread slot or from queryOnly) - do not created new transaction
        /// Used only in SystemCollections to get running query transaction
        /// </summary>
        public TransactionService GetThreadTransaction()
        {
            this.ThrowIfDisposed();
            return _slot.Value ?? _transactions.FindForThread(Environment.CurrentManagedThreadId);
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
                cleanup.Catch(transaction.Dispose);
            }

            cleanup.Catch(_slot.Dispose);
            if (cleanup.Exceptions.Count > 0) throw new AggregateException(cleanup.Exceptions);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(TransactionMonitor));
        }
    }
}
