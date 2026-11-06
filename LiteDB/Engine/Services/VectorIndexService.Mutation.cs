namespace LiteDB.Engine
{
    internal sealed partial class VectorIndexService
    {
        public void Upsert(CollectionIndex index, VectorIndexMetadata metadata, BsonDocument document, PageAddress dataBlock)
        {
            _snapshot.RequireVectorVersion();
            if (this.IsRepairDeferred(metadata))
            {
                this.RetryDeferredRepair(metadata, PageAddress.Empty);
                return;
            }
            var value = index.BsonExpr.ExecuteScalar(document, _collation);

            if (!TryExtractVector(value, metadata.Dimensions, out var vector))
            {
                this.Delete(metadata, dataBlock);
                return;
            }

            this.Delete(metadata, dataBlock);
            if (this.IsRepairDeferred(metadata)) return;
            this.Insert(metadata, dataBlock, vector);
        }

        public void Delete(VectorIndexMetadata metadata, PageAddress dataBlock)
        {
            _snapshot.RequireVectorVersion();
            if (this.IsRepairDeferred(metadata))
            {
                this.RetryDeferredRepair(metadata, dataBlock);
                return;
            }
            if (!this.TryFindNode(metadata, dataBlock, out var address, out var node))
            {
                return;
            }

            this.RemoveNode(metadata, address, node);
        }

    }
}
