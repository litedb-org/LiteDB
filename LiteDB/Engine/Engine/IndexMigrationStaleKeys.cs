using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Released engines kept a node when the old <c>==</c> found its key equal to the
        /// updated value, including the primary key of an updated document. That comparison
        /// rounded doubles to decimal and read a missing document field as null, so a
        /// document can hold 19.99m or {b:null} under a node keyed 19.99 or {a:null}. Old
        /// equality only relaxed numbers and the containers that hold them, so other key
        /// types always equal their documents and need no document read.
        /// </summary>
        private static bool MayHoldStaleKey(BsonValue key) => key.IsNumber || key.IsDocument || key.IsArray;

        /// <summary>
        /// Primary-key positions of documents whose primary or member-path keys no longer
        /// equal their document under the current comparer. Migration reuses member-path
        /// nodes by their stored keys, so these must be regenerated from the document.
        /// </summary>
        private List<PageAddress> FindStaleMemberPathDocuments(Snapshot snapshot, IndexService indexer, IndexMigrationCapacity capacity)
        {
            var collation = _header.Pragmas.Collation;
            var memberPaths = snapshot.CollectionPage.GetCollectionIndexes()
                .Where(x => x.IndexType == 0 && IndexExpressionIdentity.IsMemberPath(x.BsonExpr))
                .ToArray();
            var slots = new HashSet<byte>(memberPaths.Select(x => x.Slot));
            var data = new DataService(snapshot, _disk.MAX_ITEMS_COUNT);
            var stale = new List<PageAddress>();
            long repairBytes = 0;

            foreach (var primary in indexer.FindAll(snapshot.CollectionPage.PK, LiteDB.Query.Ascending))
            {
                // Capture addresses and keys before any safepoint can release these nodes.
                var position = primary.Position;
                var stored = indexer.GetNodeList(position).Where(x => slots.Contains(x.Slot))
                    .Select(x => new KeyValuePair<byte, BsonValue>(x.Slot, x.Key)).ToArray();
                if (stored.Any(x => MayHoldStaleKey(x.Value)))
                {
                    BsonDocument document;
                    using (var reader = new BufferReader(data.Read(primary.DataBlock)))
                        document = reader.ReadDocument().GetValue();
                    var keys = memberPaths.Select(index => new KeyValuePair<CollectionIndex, BsonValue[]>(
                        index, index.BsonExpr.GetIndexKeys(document, collation).ToArray())).ToArray();
                    if (keys.Any(x => !SameKeys(stored.Where(s => s.Key == x.Key.Slot).Select(s => s.Value).ToArray(), x.Value, collation)))
                    {
                        foreach (var key in keys.SelectMany(x => x.Value))
                        {
                            if (key.IsMinValue || key.IsMaxValue || IndexNode.GetKeyLength(key, true) > MAX_INDEX_KEY_LENGTH)
                                throw LiteException.InvalidIndexKey("Invalid key while migrating collection " + snapshot.CollectionName);
                            repairBytes += IndexNode.GetNodeLength(MAX_LEVEL_LENGTH, key, out _) + BasePage.SLOT_SIZE;
                        }
                        stale.Add(position);
                    }
                }
                snapshot.Safepoint();
            }
            capacity?.AddOrdinaryIndex(repairBytes, 0);
            return stale;
        }

        private static bool SameKeys(BsonValue[] stored, BsonValue[] expected, Collation collation)
        {
            if (stored.Length != expected.Length) return false;
            var left = stored.OrderBy(x => x, collation).ToArray();
            var right = expected.OrderBy(x => x, collation).ToArray();
            for (var i = 0; i < left.Length; i++)
                if (left[i].CompareTo(right[i], collation) != 0) return false;
            return true;
        }

        /// <summary>
        /// Replaces every primary and member-path node of the stale documents with keys
        /// generated from the document. Other indexes are regenerated later. Nodes are
        /// linked into the still-unordered lists here; <see cref="ReorderIndex"/> orders them.
        /// </summary>
        private void RepairStaleMemberPathDocuments(Snapshot snapshot, IndexService indexer, List<PageAddress> stale)
        {
            if (stale.Count == 0) return;
            var collation = _header.Pragmas.Collation;
            var col = snapshot.CollectionPage;
            var memberPaths = col.GetCollectionIndexes()
                .Where(x => x.IndexType == 0 && x.Name != "_id" && IndexExpressionIdentity.IsMemberPath(x.BsonExpr))
                .Select(x => x.Name).ToArray();
            var blocks = new List<PageAddress>(stale.Count);

            // Remove every stale node first: a stored key may equal another document's
            // corrected key, and unique inserts must only see validated document keys.
            foreach (var position in stale)
            {
                blocks.Add(indexer.GetNode(position).DataBlock);
                indexer.DeleteAll(position);
                snapshot.Safepoint();
            }

            var data = new DataService(snapshot, _disk.MAX_ITEMS_COUNT);
            foreach (var dataBlock in blocks)
            {
                BsonDocument document;
                using (var reader = new BufferReader(data.Read(dataBlock)))
                    document = reader.ReadDocument().GetValue();
                var last = indexer.AddNode(col.PK, document["_id"], dataBlock, null);
                foreach (var name in memberPaths)
                {
                    var index = col.GetCollectionIndex(name);
                    foreach (var key in index.BsonExpr.GetIndexKeys(document, collation))
                        last = indexer.AddNode(index, key, dataBlock, last);
                }
                snapshot.Safepoint();
            }
        }
    }
}
