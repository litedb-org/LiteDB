using System.Threading;

namespace LiteDB.Engine
{
    /// <summary>
    /// Identifies who owns a transaction: the creating thread for legacy and automatic
    /// transactions, or the explicit handle whose sequential calls may run on any thread.
    /// </summary>
    internal sealed class TransactionOwner
    {
        internal readonly Thread Thread;
        internal readonly object Explicit;
        internal readonly TransactionSlot Slot;

        /// <summary>The owner of the transaction's admission lease.</summary>
        internal object Admission => (object)Explicit ?? Thread;

        internal TransactionOwner(object explicitContext, TransactionSlot slot)
        {
            Explicit = explicitContext;
            Thread = explicitContext == null ? Thread.CurrentThread : null;
            Slot = slot;
        }
    }

    /// <summary>The transaction resolved for one owner: a legacy thread or an explicit handle.</summary>
    internal sealed class TransactionSlot
    {
        internal TransactionService Transaction;
        internal bool ExplicitAborted;
        // A slot's owner never changes (its thread, or its handle): allocate it once.
        internal TransactionOwner Owner;
    }
}
