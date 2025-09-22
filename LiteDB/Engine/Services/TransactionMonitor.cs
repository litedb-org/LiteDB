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
        private readonly AsyncLocal<TransactionContext> _context = new AsyncLocal<TransactionContext>();

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
            var context = create ? this.GetOrCreateContext() : _context.Value;
            TransactionService transaction = null;

            if (context != null)
            {
                if (queryOnly)
                {
                    transaction = context.WriteTransaction ?? PeekQuery(context);
                }
                else
                {
                    transaction = context.WriteTransaction;
                }
            }

            if (transaction == null && create)
            {
                transaction = this.CreateTransaction(queryOnly, context!);
                isNew = true;
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
            transaction.Dispose();

            lock (_transactions)
            {
                _transactions.Remove(transaction.TransactionID);

                // return freePages used area
                _freePages += transaction.MaxTransactionSize;
            }

            if (transaction.Context != null)
            {
                this.UnregisterTransaction(transaction);
            }

            _locker.ExitTransaction();
        }

        /// <summary>
        /// Get transaction from current thread (from thread slot or from queryOnly) - do not created new transaction
        /// Used only in SystemCollections to get running query transaction
        /// </summary>
        public TransactionService GetThreadTransaction()
        {
            var context = _context.Value;

            if (context != null)
            {
                return context.WriteTransaction ?? PeekQuery(context);
            }

            lock (_transactions)
            {
                return _transactions.Values.FirstOrDefault();
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

        private TransactionService CreateTransaction(bool queryOnly, TransactionContext context)
        {
            TransactionService transaction;
            int initialSize;

            lock (_transactions)
            {
                if (_transactions.Count >= MAX_OPEN_TRANSACTIONS) throw new LiteException(0, "Maximum number of transactions reached");

                initialSize = this.GetInitialSize();

                transaction = new TransactionService(_header, _locker, _disk, _walIndex, initialSize, this, queryOnly);
                _transactions[transaction.TransactionID] = transaction;
            }

            try
            {
                _locker.EnterTransaction();
            }
            catch
            {
                this.CleanupFailedTransaction(transaction);
                throw;
            }

            transaction.Context = context;
            this.RegisterTransaction(context, transaction);

            return transaction;
        }

        private void RegisterTransaction(TransactionContext context, TransactionService transaction)
        {
            if (transaction.QueryOnly)
            {
                context.QueryTransactions.Push(transaction);
            }
            else
            {
                context.WriteTransaction = transaction;
            }
        }

        private void CleanupFailedTransaction(TransactionService transaction)
        {
            transaction.Dispose();

            lock (_transactions)
            {
                _freePages += transaction.MaxTransactionSize;
                _transactions.Remove(transaction.TransactionID);
            }

            transaction.Context = null;
        }

        private void UnregisterTransaction(TransactionService transaction)
        {
            var context = transaction.Context;

            if (context == null)
            {
                return;
            }

            if (transaction.QueryOnly)
            {
                if (context.QueryTransactions.Count > 0 && ReferenceEquals(context.QueryTransactions.Peek(), transaction))
                {
                    context.QueryTransactions.Pop();
                }
                else if (context.QueryTransactions.Count > 0)
                {
                    var buffer = new Stack<TransactionService>();

                    while (context.QueryTransactions.Count > 0)
                    {
                        var current = context.QueryTransactions.Pop();

                        if (!ReferenceEquals(current, transaction))
                        {
                            buffer.Push(current);
                        }
                    }

                    while (buffer.Count > 0)
                    {
                        context.QueryTransactions.Push(buffer.Pop());
                    }
                }
            }
            else if (ReferenceEquals(context.WriteTransaction, transaction))
            {
                context.WriteTransaction = null;
            }

            transaction.Context = null;

            if (ReferenceEquals(_context.Value, context) && context.WriteTransaction == null && context.QueryTransactions.Count == 0)
            {
                _context.Value = null;
            }
        }

        private TransactionContext GetOrCreateContext()
        {
            var context = _context.Value;

            if (context == null)
            {
                context = new TransactionContext();
                _context.Value = context;
            }

            return context;
        }

        private static TransactionService PeekQuery(TransactionContext context)
        {
            return context.QueryTransactions.Count > 0 ? context.QueryTransactions.Peek() : null;
        }

        internal sealed class TransactionContext
        {
            public TransactionService WriteTransaction;
            public Stack<TransactionService> QueryTransactions { get; } = new Stack<TransactionService>();
        }
    }
}
