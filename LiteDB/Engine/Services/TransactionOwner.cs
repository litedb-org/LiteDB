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
        internal readonly TransactionContext Explicit;
        internal readonly TransactionSlot Slot;

        /// <summary>The owner of the transaction's admission lease.</summary>
        internal object Admission => (object)Explicit ?? Thread;

        internal TransactionOwner(TransactionContext explicitContext, TransactionSlot slot)
        {
            Explicit = explicitContext;
            Thread = explicitContext == null ? Thread.CurrentThread : null;
            Slot = slot;
        }
    }
}
