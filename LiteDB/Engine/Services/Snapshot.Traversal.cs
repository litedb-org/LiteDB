namespace LiteDB.Engine
{
    internal partial class Snapshot
    {
        // Count real transaction allocations, including reused pages and pages not in the WAL yet.
        // Do not trust the on-disk LastPageID as a loop bound: corrupt headers can inflate it.
        internal ulong AdditionalTraversalItemsCount => (ulong)_transPages.NewPages.Count * byte.MaxValue;
    }
}
