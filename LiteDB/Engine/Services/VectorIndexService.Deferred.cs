using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal sealed partial class VectorIndexService
    {
        // No durable cursor or second graph is introduced. Restart a bounded
        // reconciliation on each later write. Never load a retained DataBlock:
        // it may have been deleted, relocated or reused since deferral.
        private void RetryDeferredRepair(VectorIndexMetadata metadata, PageAddress excluded)
        {
            var remaining = ConnectivityRepairBudget;
            var addresses = new HashSet<PageAddress>();
            var pending = new Stack<PageAddress>();
            if (!metadata.Root.IsEmpty) pending.Push(metadata.Root);
            while (pending.Count > 0)
            {
                var address = pending.Pop();
                if (!addresses.Add(address)) continue;
                if (--remaining < 0) return;
                var node = this.GetNode(address);
                for (var level = 0; level < node.LevelCount; level++)
                    foreach (var neighbor in node.GetNeighbors(level))
                        if (!neighbor.IsEmpty && !addresses.Contains(neighbor)) pending.Push(neighbor);
                _snapshot.Safepoint();
            }

            var index = this.GetRepairIndex(metadata);
            var indexer = new IndexService(_snapshot, _collation, _snapshot.MaxItemsCount);
            var lookup = new DatafileLookup(new DataService(_snapshot, _snapshot.MaxItemsCount), false, null);
            var vectors = new List<(PageAddress Block, float[] Vector)>();
            foreach (var pk in indexer.FindAll(_snapshot.CollectionPage.PK, Query.Ascending))
            {
                // Charge M units per document, reserving reconstruction input and
                // neighbour work. The entire attempt remains independent of N.
                remaining -= VectorIndexNode.MaxNeighborsPerLevel;
                if (remaining < 0) return;
                var block = pk.DataBlock;
                if (block != excluded)
                {
                    var document = lookup.Load(block);
                    var value = index.BsonExpr.ExecuteScalar(document, _collation);
                    if (TryExtractVector(value, metadata.Dimensions, out var vector)) vectors.Add((block, vector));
                }
                _snapshot.Safepoint();
            }

            // All inputs fit. Reconstruct using normal HNSW construction, retaining
            // its levels and neighbour heuristics. Publication and old-node release
            // share the caller's transaction; rollback restores both tree and bit.
            foreach (var address in addresses)
            {
                this.ReleaseNode(metadata, this.GetNode(address));
                _snapshot.Safepoint();
            }
            metadata.Root = PageAddress.Empty;
            foreach (var item in vectors) this.Insert(metadata, item.Block, item.Vector);
            index.VectorRepairDeferred = false;
            _snapshot.CollectionPage.IsDirty = true;
        }
    }
}
