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
        /// Starts an independent synchronous transaction. Ordinary database collections do not
        /// enlist: use the returned handle's collections. Sequential thread handoff is supported;
        /// overlapping use of one handle is rejected. Disposing an uncommitted handle rolls it back.
        /// </summary>
        public ILiteTransaction BeginTransaction()
        {
            _transactionHandles.ThrowIfClosed();
            // A Shared begin may wait for writer ownership: do not hold up database close meanwhile.
            var resources = this.OpenTransactionResources();
            try { _transactionHandles.EnterPending(); }
            catch (Exception error)
            {
                try { resources.Dispose(); }
                catch (Exception cleanup) { error.Data["LiteDB.TransactionOpenCleanup"] = cleanup; }
                throw;
            }
            LiteTransaction transaction;
            try
            {
                transaction = new LiteTransaction(resources, this.Mapper, _transactionHandles);
            }
            catch
            {
                _transactionHandles.Abandon();
                throw;
            }
            _transactionHandles.Register(transaction);
            return transaction;
        }

        private TransactionResources OpenTransactionResources()
        {
            if (_engine is SharedEngine shared) return shared.OpenTransactionResources();
            if (_engine is LiteEngine engine) return new TransactionResources(engine, () => { });
            // Never emulate a handle over a legacy thread-bound API of an unknown engine.
            throw new NotSupportedException("This engine does not support thread-independent transaction handles.");
        }
    }
}
