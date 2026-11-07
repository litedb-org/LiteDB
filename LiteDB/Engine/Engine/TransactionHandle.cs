using System;
using System.Linq;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        // Never identify a facade by its shared core: sibling facades share services,
        // while each monitor view and each handle retain independent ownership.
        internal void ValidateHandleContext(TransactionContext context)
        {
            _state.Validate();
            _monitor.ValidateOwner();
            if (!ReferenceEquals(context.Engine, this) ||
                !ReferenceEquals(context.Host, (object)_host ?? _disk) ||
                !ReferenceEquals(context.Facade, _monitor.Owner) ||
                !ReferenceEquals(context.Lease.Facade, _monitor.Owner) ||
                (context.Transaction != null && !ReferenceEquals(context.Transaction.Lease, context.Lease)))
                throw new InvalidOperationException("Handle context does not belong to this host and facade.");
        }

        internal TransactionContext BeginHandleTransaction()
        {
            this.ValidatePublicDispatch();
            _state.Validate();
            var context = new TransactionContext(this, (object)_host ?? _disk, _monitor.Owner);
            using var budget = new TransactionWaitBudget(HandleTimeout);
            context.Transaction = _monitor.CreateTransaction(context.Lease);
            context.Transaction.ExplicitTransaction = true;
            return context;
        }

        internal void ReleaseDetachedHandleTransaction(TransactionContext context)
        {
            if (context.Transaction?.State != TransactionState.Active &&
                _monitor.GetTransactionsSnapshot().Contains(context.Transaction))
                _monitor.ReleaseTransaction(context.Transaction);
        }

        internal TimeSpan HandleTimeout => _header.Pragmas.Timeout;

        internal bool IsHandleReadOnly => _settings.ReadOnly || _settings.ReadOnlyStorage;
        internal Exception HandleUnavailableFailure()
        {
            try { _state.Validate(); _monitor.ValidateOwner(); return null; }
            catch (Exception error) { return error; }
        }
        internal void ThrowIfHandleUnavailable() { _state.Validate(); _monitor.ValidateOwner(); }

        internal string[] GetTransactionCollectionNames() => AutoReadTransaction(transaction =>
            _header.GetCollections().Select(x => x.Key).Concat(transaction.Snapshots.Where(x => x.CollectionPage != null).Select(x => x.CollectionName)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

        private void ValidatePublicDispatch()
        {
            if (!TransactionContext.ConsumeDispatch(this) && TransactionContext.For(this) != null)
                throw new InvalidOperationException("Raw engine reentry cannot inherit a handle transaction.");
        }

        private void RefuseHandleMaintenance()
        {
            if (TransactionContext.For(this) != null)
                throw new NotSupportedException("This operation is unavailable through a transaction handle.");
        }
    }
}
