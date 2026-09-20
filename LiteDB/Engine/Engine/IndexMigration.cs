using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB.Vector;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        private void MigrateIndexOrdering()
        {
            if (_header.Pragmas.IndexOrderVersion == EnginePragmas.INDEX_ORDER_VERSION)
            {
                this.ValidateLegacyCollation();
                return;
            }

            if (_settings.ReadOnly)
                throw new LiteException(0, "Database index ordering/collation requires migration. " +
                    "Open the database writable once to automatically rebuild its indexes.");

            // Traverse links, never seek using the new comparer in an old skip list.
            // Inspect all structures and unique keys before any persistent mutation.
            this.ValidateLegacyCollation(migrating: true);
            var capacity = new IndexMigrationCapacity(_header, _settings.IndexMigrationLimitSize);
            var validation = _monitor.GetTransaction(true, true, out _);
            try
            {
                foreach (var collection in _header.GetCollections())
                {
                    var snapshot = validation.CreateSnapshot(LockMode.Read, collection.Key, false);
                    var indexer = new IndexService(snapshot, _header.Pragmas.Collation, _disk.MAX_ITEMS_COUNT);
                    foreach (var index in snapshot.CollectionPage.GetCollectionIndexes())
                    {
                        if (index.IndexType != 0)
                        {
                            this.ValidateVectorMigration(snapshot, indexer, index, capacity);
                            continue;
                        }
                        var memberPath = IndexExpressionIdentity.IsMemberPath(index.BsonExpr);
                        if (memberPath && !index.Unique) continue;
                        using (var sort = index.Unique ? new SortService(_sortDisk,
                            new[] { LiteDB.Query.Ascending }, _header.Pragmas) : null)
                        {
                            var keys = this.GetMigrationKeys(snapshot, indexer, index);
                            if (sort != null)
                            {
                                sort.Insert(keys);
                                keys = sort.Sort();
                            }
                            BsonValue previous = null;
                            long maximumNodeBytes = 0;
                            foreach (var item in keys)
                            {
                                if (index.Unique && previous != null &&
                                    previous.CompareTo(item.Key, _header.Pragmas.Collation) == 0)
                                    throw LiteException.IndexDuplicateKey(index.Name, item.Key);
                                maximumNodeBytes += IndexNode.GetNodeLength(MAX_LEVEL_LENGTH, item.Key, out _) + BasePage.SLOT_SIZE;
                                previous = item.Key;
                            }
                            if (!memberPath)
                            {
                                var pages = new HashSet<uint> { index.Head.PageID, index.Tail.PageID };
                                foreach (var node in indexer.FindAll(index, LiteDB.Query.Ascending))
                                {
                                    pages.Add(node.Position.PageID);
                                    snapshot.Safepoint();
                                }
                                capacity.AddOrdinaryIndex(maximumNodeBytes, pages.Count);
                            }
                        }
                    }
                }
                capacity.Validate(validation.CreateSnapshot(LockMode.Read, "$migration_capacity", false), _header);
            }
            finally { _monitor.ReleaseTransaction(validation); }

            _disk.TrimTrailingPages();
            // Old binaries must reject even an unconfirmed migration WAL. The order
            // revision stays zero until the same transaction commits every index.
            _disk.PromoteFileFormat(HeaderPage.INDEX_FILE_VERSION);
            _header.EnsureVersion(HeaderPage.INDEX_FILE_VERSION);

            this.BeginTrans();
            try
            {
                var transaction = _monitor.GetTransaction(true, false, out _);
                transaction.Pages.IndexMigrationLimitSize = _settings.IndexMigrationLimitSize;
                foreach (var collection in _header.GetCollections())
                {
                    var snapshot = transaction.CreateSnapshot(LockMode.Write, collection.Key, false);
                    var indexer = new IndexService(snapshot, _header.Pragmas.Collation, _disk.MAX_ITEMS_COUNT);
                    foreach (var index in snapshot.CollectionPage.GetCollectionIndexes())
                    {
                        if (index.IndexType == 0 && IndexExpressionIdentity.IsMemberPath(index.BsonExpr))
                            this.ReorderIndex(snapshot, indexer, index);
                    }
                    var indexes = snapshot.CollectionPage.GetCollectionIndexes()
                        .Where(x => !IndexExpressionIdentity.IsMemberPath(x.BsonExpr))
                        .Select(x => new
                        {
                            x.Name, x.Expression, x.Unique,
                            Vector = snapshot.CollectionPage.GetVectorIndexMetadata(x.Name)
                        }).ToArray();

                    foreach (var index in indexes)
                    {
                        // Re-evaluate expressions and multikey distinctness from BSON,
                        // since the old comparison could have omitted keys entirely.
                        if (index.Vector == null)
                            this.RebuildOrdinaryIndex(snapshot, indexer,
                                snapshot.CollectionPage.GetCollectionIndex(index.Name));
                        else
                        {
                            this.DropIndex(collection.Key, index.Name);
                            this.EnsureVectorIndex(collection.Key, index.Name, index.Expression,
                                new VectorIndexOptions(index.Vector.Dimensions, index.Vector.Metric));
                        }
                    }
                }
                transaction.Pages.Commit += header =>
                {
                    if (_settings.IndexMigrationLimitSize.HasValue)
                        header.Pragmas.Set(Pragmas.LIMIT_SIZE, _settings.IndexMigrationLimitSize.Value, true);
                    header.Pragmas.CompleteIndexMigration();
                };
                this.Commit();
            }
            catch (Exception ex)
            {
                // Never append rollback pages after a failed/torn migration write.
                // Fatal I/O closes the engine; the next open restores committed WAL.
                if (_state.Handle(ex) && !_state.Disposed) this.Rollback();
                throw;
            }
        }

        private void RebuildOrdinaryIndex(Snapshot snapshot, IndexService indexer, CollectionIndex index)
        {
            snapshot.RetainEmptyIndexPages = true;
            // Keep the sentinels and metadata. Empty indexes need no replacement
            // pages, and node deletion returns unused pages to the database on commit.
            foreach (var primary in indexer.FindAll(snapshot.CollectionPage.PK, LiteDB.Query.Ascending))
            {
                var remove = new HashSet<PageAddress>(indexer.GetNodeList(primary.Position)
                    .Where(x => x.Slot == index.Slot).Select(x => x.Position));
                if (remove.Count != 0) indexer.DeleteList(primary.Position, remove);
                snapshot.Safepoint();
            }
            var data = new DataService(snapshot, _disk.MAX_ITEMS_COUNT);
            foreach (var primary in indexer.FindAll(snapshot.CollectionPage.PK, LiteDB.Query.Ascending))
            {
                var position = primary.Position;
                var dataBlock = primary.DataBlock;
                using (var reader = new BufferReader(data.Read(dataBlock)))
                {
                    var document = reader.ReadDocument().GetValue();
                    var last = indexer.GetNodeList(position).Last();
                    foreach (var key in index.BsonExpr.GetIndexKeys(document, _header.Pragmas.Collation))
                        last = indexer.AddNode(index, key, dataBlock, last);
                }
                snapshot.Safepoint();
            }
            snapshot.ReleaseEmptyIndexPages(index);
            snapshot.CollectionPage.IsDirty = true;
        }

        private void ValidateVectorMigration(Snapshot snapshot, IndexService indexer, CollectionIndex index, IndexMigrationCapacity capacity)
        {
            if (IndexExpressionIdentity.IsMemberPath(index.BsonExpr)) return;
            var data = new DataService(snapshot, _disk.MAX_ITEMS_COUNT);
            foreach (var node in indexer.FindAll(snapshot.CollectionPage.PK, LiteDB.Query.Ascending))
            {
                using (var reader = new BufferReader(data.Read(node.DataBlock)))
                    index.BsonExpr.ExecuteScalar(reader.ReadDocument().GetValue(), _header.Pragmas.Collation);
                capacity.AddVectorDocument(snapshot.CollectionPage.GetVectorIndexMetadata(index.Name).Dimensions);
                snapshot.Safepoint();
            }
        }

        private IEnumerable<KeyValuePair<BsonValue, PageAddress>> GetMigrationKeys(
            Snapshot snapshot, IndexService indexer, CollectionIndex index)
        {
            var data = new DataService(snapshot, _disk.MAX_ITEMS_COUNT);
            foreach (var node in indexer.FindAll(snapshot.CollectionPage.PK, LiteDB.Query.Ascending))
            {
                // Capture addresses before any safepoint can release this node.
                var position = node.Position;
                using (var reader = new BufferReader(data.Read(node.DataBlock)))
                {
                    var document = reader.ReadDocument().GetValue();
                    foreach (var key in index.BsonExpr.GetIndexKeys(document, _header.Pragmas.Collation))
                    {
                        if (key.IsMinValue || key.IsMaxValue ||
                            IndexNode.GetKeyLength(key, true) > MAX_INDEX_KEY_LENGTH)
                            throw LiteException.InvalidIndexKey("Invalid key while migrating index " + index.Name);
                        yield return new KeyValuePair<BsonValue, PageAddress>(key, position);
                    }
                }
                snapshot.Safepoint();
            }
        }

        private void ReorderIndex(Snapshot snapshot, IndexService indexer, CollectionIndex index)
        {
            using (var sort = new SortService(_sortDisk, new[] { LiteDB.Query.Ascending }, _header.Pragmas))
            {
                IEnumerable<KeyValuePair<BsonValue, PageAddress>> Keys()
                {
                    foreach (var node in indexer.FindAll(index, LiteDB.Query.Ascending))
                    {
                        yield return new KeyValuePair<BsonValue, PageAddress>(node.Key, node.Position);
                        snapshot.Safepoint();
                    }
                }
                // A proven scalar member path emits exactly one unchanged value per
                // document. Reuse its pages, including when LIMIT_SIZE leaves no room.
                sort.Insert(Keys());
                var previous = Enumerable.Repeat(index.Head, MAX_LEVEL_LENGTH).ToArray();
                foreach (var item in sort.Sort())
                {
                    var node = indexer.GetNode(item.Value);
                    for (byte level = 0; level < node.Levels; level++)
                    {
                        indexer.GetNode(previous[level]).SetNext(level, node.Position);
                        node.SetPrev(level, previous[level]);
                        previous[level] = node.Position;
                    }
                    snapshot.Safepoint();
                }
                for (byte level = 0; level < MAX_LEVEL_LENGTH; level++)
                {
                    indexer.GetNode(previous[level]).SetNext(level, index.Tail);
                    indexer.GetNode(index.Tail).SetPrev(level, previous[level]);
                }
            }
        }
    }
}
