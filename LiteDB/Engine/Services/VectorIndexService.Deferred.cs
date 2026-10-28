namespace LiteDB.Engine
{
    internal sealed partial class VectorIndexService
    {
        // Restart bounded reconciliation from physical collection ownership and
        // current documents; retained DataBlocks may already have been reused.
        private void RetryDeferredRepair(VectorIndexMetadata metadata, PageAddress excluded)
        {
            this.ReconcileVectorPages(null, excluded, bounded: true);
        }
    }
}
