using System;
using System.Linq;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        private TransactionService GetTransactionForCompletion()
        {
            var transaction = _monitor.GetTransaction(false, false, out _);
            if (transaction == null && _monitor.Transactions.Any(candidate =>
                candidate.ExplicitTransaction && candidate.State == TransactionState.Active &&
                candidate.ThreadID != Environment.CurrentManagedThreadId))
            {
                throw new LiteException(0, "No transaction belongs to this thread, but an explicit transaction is open on another thread. " +
                    "BeginTrans, writes, Commit and Rollback must run synchronously on the same thread; do not await inside the transaction.");
            }
            return transaction;
        }
    }
}
