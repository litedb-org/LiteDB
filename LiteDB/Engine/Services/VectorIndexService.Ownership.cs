using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal sealed partial class VectorIndexService
    {
        // Bound the reverse census independently of graph size. Failure to
        // certify ownership retains the node and selects exact document queries.
        internal const int ReverseScanPageBudget = ConnectivityRepairBudget * 8;

        private bool CertifyIncomingLinks(PageAddress removed)
        {
            var pages = _snapshot.GetVectorPages(ReverseScanPageBudget);
            if (pages == null) return false;
            var outgoing = Enumerable.Range(0, VectorIndexNode.MaxLevels)
                .Select(level => this.GetNode(removed).GetNeighbors(level).ToArray()).ToArray();
            foreach (var id in pages)
            {
                var page = _snapshot.GetPage<VectorIndexPage>(id);
                foreach (var candidate in page.GetNodes())
                    for (var level = 0; level < VectorIndexNode.MaxLevels; level++)
                        if (candidate.GetNeighbors(level).Contains(removed) && !outgoing[level].Contains(candidate.Position))
                            return false;
                _snapshot.Safepoint();
            }
            return true;
        }

        // Pages carry collection ownership, not index ownership. Reconstruct
        // sibling vector indexes too: guessing which index owns an orphan full
        // page could either leak it or release another index's live nodes.
        private bool ReconcileVectorPages(VectorIndexMetadata dropped, PageAddress excluded, bool bounded)
        {
            var pages = _snapshot.GetVectorPages(bounded ? ConnectivityRepairBudget : int.MaxValue);
            if (pages == null) return false;
            var indexes = _snapshot.CollectionPage.GetVectorIndexes().ToArray();
            var remaining = ConnectivityRepairBudget;
            foreach (var id in pages)
            {
                remaining -= _snapshot.GetPage<VectorIndexPage>(id).ItemsCount;
                if (bounded && remaining < 0) return false;
                _snapshot.Safepoint();
            }
            var vectors = new List<(VectorIndexMetadata Metadata, PageAddress Block, float[] Vector)>();
            var indexer = new IndexService(_snapshot, _collation, _snapshot.MaxItemsCount);
            var lookup = new DatafileLookup(new DataService(_snapshot, _snapshot.MaxItemsCount), false, null);
            var survivors = indexes.Where(x => x.Metadata != dropped).ToArray();
            foreach (var pk in indexer.FindAll(_snapshot.CollectionPage.PK, Query.Ascending))
            {
                remaining -= VectorIndexNode.MaxNeighborsPerLevel * survivors.Length;
                if (bounded && remaining < 0) return false;
                var block = pk.DataBlock;
                if (block != excluded && survivors.Length > 0)
                {
                    var document = lookup.Load(block);
                    foreach (var item in survivors)
                        if (TryExtractVector(item.Index.BsonExpr.ExecuteScalar(document, _collation),
                            item.Metadata.Dimensions, out var vector)) vectors.Add((item.Metadata, block, vector));
                }
                _snapshot.Safepoint();
            }

            // Planning completed without writes. Retire every physical node,
            // including disconnected components and external vector payloads.
            foreach (var item in indexes)
            {
                item.Metadata.Root = PageAddress.Empty;
                item.Metadata.Reserved = uint.MaxValue;
            }
            _snapshot.CollectionPage.IsDirty = true;
            foreach (var id in pages)
            {
                var addresses = _snapshot.GetPage<VectorIndexPage>(id).GetNodes().Select(x => x.Position).ToArray();
                foreach (var address in addresses)
                {
                    this.ReleaseVectorData(this.GetNode(address));
                    _snapshot.Safepoint();
                }
                _snapshot.DeleteVectorPage(_snapshot.GetPage<VectorIndexPage>(id));
                _snapshot.Safepoint();
            }
            foreach (var vector in vectors) this.Insert(vector.Metadata, vector.Block, vector.Vector);
            foreach (var item in indexes) item.Index.VectorRepairDeferred = false;
            return true;
        }
    }
}
