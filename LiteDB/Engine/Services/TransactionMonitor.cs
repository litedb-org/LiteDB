using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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

        private int _freePages;
        private readonly int _initialSize;

        // expose open transactions
        public ICollection<TransactionService> Transactions => _transactions.Values;
        public int FreePages => _freePages;
        public int InitialSize => _initialSize;

        public TransactionMonitor(HeaderPage header, LockService locker, DiskService disk, WalIndexService walIndex)
        {
            _header = header;
            _locker = locker;
            _disk = disk;
            _walIndex = walIndex;

            // initialize free pages with all avaiable pages in memory
            _freePages = MAX_TRANSACTION_SIZE;

            // initial size 
            _initialSize = MAX_TRANSACTION_SIZE / MAX_OPEN_TRANSACTIONS;
        }

        public TransactionService GetTransaction(bool create, bool queryOnly, out bool isNew)
        {
            var transaction = _slot.Value;

            if (create && transaction == null)
            {
                isNew = true;

                bool alreadyLock;

                // must lock _transaction before work with _transactions (GetInitialSize use _transactions)
                lock (_transactions)
                {
                    if (_transactions.Count >= MAX_OPEN_TRANSACTIONS) throw new LiteException(0, "Maximum number of transactions reached");

                    var initialSize = this.GetInitialSize();

                    // check if current thread contains any transaction
                    alreadyLock = _transactions.Values.Any(x => x.ThreadID == Environment.CurrentManagedThreadId);

                    transaction = new TransactionService(_header, _locker, _disk, _walIndex, initialSize, this, queryOnly);

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
                            // return pages
                            _freePages += transaction.MaxTransactionSize;
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
        /// Release current thread transaction
        /// </summary>
        public void ReleaseTransaction(TransactionService transaction)
        {
            // dispose current transaction
            transaction.Dispose();

            bool keepLocked;

            lock (_transactions)
            {
                // remove from "open transaction" list
                _transactions.Remove(transaction.TransactionID);

                // return freePages used area
                _freePages += transaction.MaxTransactionSize;

                // check if current thread contains more query transactions
                keepLocked = _transactions.Values.Any(x => x.ThreadID == Environment.CurrentManagedThreadId);
            }

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
        /// Get initial transaction size - get from free pages or reducing from all open transactions
        /// </summary>
        private int GetInitialSize()
        {
            if (_freePages >= _initialSize)
            {
                _freePages -= _initialSize;

                return _initialSize;
            }
            else
            {
                var sum = 0;

                // if there is no available pages, reduce all open transactions
                foreach (var trans in _transactions.Values)
                {
                    //TODO: revisar estas contas, o reduce tem que fechar 1000
                    var reduce = (trans.MaxTransactionSize / _initialSize);

                    trans.MaxTransactionSize -= reduce;

                    sum += reduce;
                }

                return sum;
            }
        }

        /// <summary>
        /// Try extend max transaction size in passed transaction ONLY if contains free pages available
        /// </summary>
        private bool TryExtend(TransactionService trans)
        {
            lock(_transactions)
            {
                if (_freePages >= _initialSize)
                {
                    trans.MaxTransactionSize += _initialSize;

                    _freePages -= _initialSize;

                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Check if transaction size reach limit AND check if is possible extend this limit
        /// </summary>
        public bool CheckSafepoint(TransactionService trans)
        {
            return 
                trans.Pages.TransactionSize >= trans.MaxTransactionSize &&
                this.TryExtend(trans) == false;
        }

        /// <summary>
        /// Dispose all open transactions
        /// </summary>
        public void Dispose()
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
    }
}