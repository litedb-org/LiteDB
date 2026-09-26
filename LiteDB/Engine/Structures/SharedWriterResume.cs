using System.Collections.Generic;

namespace LiteDB.Engine
{
    /// <summary>
    /// Detached metadata of a closed Shared writer. Contains no streams, pages, engine,
    /// leases or callbacks. Ownership transfers once to a fresh engine after validation.
    /// </summary>
    internal sealed class SharedWriterResume
    {
        internal const long MaximumWalBytes = 16 * 1024 * 1024;
        internal const int MaximumEntries = 8192;
        internal byte[] FileHeader;
        internal byte[] LogicalHeader;
        internal byte[] Confirmation;
        internal long End, Sequence, ConfirmationPosition;
        internal uint ConfirmationCount;
        internal ulong ConfirmationDigest;
        internal Dictionary<uint, long> LastLogPositions;
        internal SortedSet<long> FreeLogPositions;
        internal uint LastWalTransactionID;
        internal Dictionary<uint, List<KeyValuePair<int, long>>> Index;
        internal HashSet<uint> ConfirmedTransactions;
        internal Dictionary<int, long> ConfirmationPositions;
        internal int ReadVersion, LastTransactionID, BackfillVersion;
    }
}
