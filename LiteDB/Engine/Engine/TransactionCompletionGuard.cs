using System;
using System.Linq;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Get the calling thread's transaction for Commit/Rollback. Only Commit rejects a foreign thread:
        /// Rollback runs in catch/finally blocks, where throwing would replace the caller's real error.
        /// </summary>
        private TransactionService GetTransactionForCompletion(bool commit)
        {
            var transaction = _monitor.GetTransaction(false, false, out _);

            // A failed operation already rolled back this thread's own explicit transaction, so an
            // empty thread slot is expected here and says nothing about the other threads.
            var abortedHere = _monitor.ConsumeExplicitAbort();

            if (commit && transaction == null && !abortedHere && _monitor.Transactions.Any(candidate =>
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
