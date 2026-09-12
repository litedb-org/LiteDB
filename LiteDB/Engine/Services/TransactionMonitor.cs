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
        private readonly Dictionary<uint, TransactionService> _transactions = new Dictionary<uint, TransactionService>();
        private readonly ThreadLocal<TransactionService> _slot = new ThreadLocal<TransactionService>();

        private readonly HeaderPage _header;
        private readonly LockService _locker;
        private readonly DiskService _disk;
        private readonly WalIndexService _walIndex;

        private readonly int _transactionPageLimit;

        // Kept for internal diagnostics and repro compatibility. Return a copy
        // so callers cannot enumerate the live dictionary without its lock.
        public ICollection<TransactionService> Transactions => this.GetTransactionsSnapshot();
        public int TransactionPageLimit => _transactionPageLimit;

        public TransactionService[] GetTransactionsSnapshot()
        {
            lock (_transactions)
            {
                return _transactions.Values.ToArray();
            }
        }

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
            var transaction = _slot.Value;

            if (create && transaction == null)
            {
                isNew = true;

                bool alreadyLock;

                lock (_transactions)
                {
                    if (_transactions.Count >= MAX_OPEN_TRANSACTIONS) throw new LiteException(0, "Maximum number of transactions reached");

                    // check if current thread contains any transaction
                    alreadyLock = _transactions.Values.Any(x => x.ThreadID == Environment.CurrentManagedThreadId);

                    transaction = new TransactionService(_header, _locker, _disk, _walIndex, _transactionPageLimit, this, queryOnly);

                    // add transaction to execution transaction dict
                    _transactions[transaction.TransactionID] = transaction;
                }

                // enter in lock transaction after release _transaction lock
                if (alreadyLock == false)
                {
                    try
                    {
                        _locker.EnterTransaction();
                    }
                    catch
                    {
                        transaction.Dispose();
                        lock (_transactions)
                        {
                            _transactions.Remove(transaction.TransactionID);
                        }
                        throw;
                    }
                }

                // do not store in thread query-only transaction
                if (queryOnly == false)
                {
                    _slot.Value = transaction;
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
        public bool RemoveTransaction(TransactionService transaction)
        {
            // dispose current transaction
            transaction.Dispose();

            bool keepLocked;

            lock (_transactions)
            {
                // remove from "open transaction" list
                _transactions.Remove(transaction.TransactionID);

                // check if current thread contains more query transactions
                return keepLocked = _transactions.Values.Any(x => x.ThreadID == Environment.CurrentManagedThreadId);
            }
        }

        /// <summary>
        /// Release current thread transaction
        /// </summary>
        public void ReleaseTransaction(TransactionService transaction)
        {
            var keepLocked = RemoveTransaction(transaction);

            // unlock thread-transaction only if there is no more transactions
            if (keepLocked == false)
            {
                _locker.ExitTransaction();
            }

            // remove transaction from thread if are no queryOnly transaction
            if (transaction.QueryOnly == false)
            {
                ENSURE(_slot.Value == transaction, "current thread must contains transaction parameter");

                // clear thread slot for new transaction
                _slot.Value = null;
            }

            _disk.Cache.TrimToLimit();
        }

        /// <summary>
        /// Get transaction from current thread (from thread slot or from queryOnly) - do not created new transaction
        /// Used only in SystemCollections to get running query transaction
        /// </summary>
        public TransactionService GetThreadTransaction()
        {
            lock (_transactions)
            {
                return
                    _slot.Value ??
                    _transactions.Values.FirstOrDefault(x => x.ThreadID == Environment.CurrentManagedThreadId);
            }
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
            lock (_transactions)
            {
                if (_transactions.Count > 0)
                {
                    foreach (var transaction in _transactions.Values)
                    {
                        transaction.Dispose();
                    }

                    _transactions.Clear();
                }
            }

            _slot.Dispose();
        }
    }
}
