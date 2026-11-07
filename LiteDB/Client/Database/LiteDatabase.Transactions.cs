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
            _transactionHandles.EnterPending();
            try
            {
                if (!(_engine is LiteEngine engine))
                    throw new NotSupportedException("BeginTransaction() currently supports Direct, memory and stream engines only; Shared and custom engines are not supported.");
                var transaction = TransactionContext.Unbound(() => new LiteTransaction(engine, Mapper, _transactionHandles));
                _transactionHandles.Register(transaction);
                return transaction;
            }
            catch { _transactionHandles.Abandon(); throw; }
        }
    }
}
