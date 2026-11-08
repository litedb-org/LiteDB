using System;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class LiteDatabase : ILiteTransactionProvider
    {
        private readonly TransactionHandles _transactionHandles = new TransactionHandles();

#if DEBUG || TESTING
        internal TransactionHandles TransactionHandles => _transactionHandles;
#endif

        /// <summary>
        /// Start an independent synchronous transaction. Use its bound collections; sequential
        /// thread handoff is supported. Disposing an active handle rolls back its changes.
        /// </summary>
        public ILiteTransaction BeginTransaction()
        {
            Utils.Reachability.Sometimes("api:ILiteTransactionProvider.BeginTransaction");
            using var begin = _transactionHandles.EnterPending();
            try
            {
                var transaction = TransactionContext.Unbound(() =>
                {
                    if (_engine is LiteEngine engine) return new LiteTransaction(engine, Mapper, _transactionHandles);
                    if (_engine is SharedEngine shared) return new LiteTransaction(shared, Mapper, _transactionHandles);
                    throw new NotSupportedException("This custom engine does not support transaction handles.");
                });
                if (!_transactionHandles.Register(transaction))
                {
                    transaction.Dispose();
                    throw new ObjectDisposedException(nameof(LiteDatabase));
                }
                return transaction;
            }
            catch { _transactionHandles.Abandon(); throw; }
        }
    }
}
