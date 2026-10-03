using System;
using System.Linq;
using System.Threading;

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

            // The diagnostic is legacy-only: a handle completes exactly its own transaction,
            // and a handle's transaction is never a candidate for a thread's legacy completion.
            if (commit && transaction == null && !abortedHere && TransactionContext.For(this) == null &&
                _monitor.Transactions.Any(candidate =>
                candidate.Owner.Explicit == null && candidate.ExplicitTransaction && candidate.State == TransactionState.Active &&
                candidate.OwnerThread != Thread.CurrentThread))
            {
                throw new LiteException(0, "No transaction belongs to this thread, but an explicit transaction is open on another thread. " +
                    "BeginTrans, writes, Commit and Rollback must run synchronously on the same thread; do not await inside the transaction.");
            }
            return transaction;
        }
    }
}
