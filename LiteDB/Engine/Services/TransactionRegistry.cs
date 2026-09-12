using System;
using System.Collections.Generic;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Bounded transaction registration without a monitor lock. Each slot owns
    /// one transaction reference; readers copy references with acquire semantics
    /// and never enumerate a collection that another thread can resize.
    /// </summary>
    internal sealed class TransactionRegistry
    {
        private readonly TransactionService[] _slots = new TransactionService[MAX_OPEN_TRANSACTIONS];
        private int _count;

        public void Add(TransactionService transaction)
        {
            // Reserve capacity before publishing. A moving vacancy must not
            // cause a spurious "full" result during a single slot scan.
            while (true)
            {
                var count = Volatile.Read(ref _count);
                if (count == _slots.Length) throw new LiteException(0, "Maximum number of transactions reached");
                if (Interlocked.CompareExchange(ref _count, count + 1, count) == count) break;
            }

            while (true)
            {
                for (var i = 0; i < _slots.Length; i++)
                {
                    if (Volatile.Read(ref _slots[i]) == null &&
                        Interlocked.CompareExchange(ref _slots[i], transaction, null) == null) return;
                }
            }
        }

        public void Remove(TransactionService transaction)
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                if (ReferenceEquals(Volatile.Read(ref _slots[i]), transaction) &&
                    ReferenceEquals(Interlocked.CompareExchange(ref _slots[i], null, transaction), transaction))
                {
                    Interlocked.Decrement(ref _count);
                    return;
                }
            }
        }

        public TransactionService FindForThread(int threadID)
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                var transaction = Volatile.Read(ref _slots[i]);
                if (transaction?.ThreadID == threadID) return transaction;
            }

            return null;
        }

        public ICollection<TransactionService> Snapshot()
        {
            var transactions = new List<TransactionService>();
            for (var i = 0; i < _slots.Length; i++)
            {
                var transaction = Volatile.Read(ref _slots[i]);
                if (transaction != null) transactions.Add(transaction);
            }

            return transactions;
        }
    }
}
