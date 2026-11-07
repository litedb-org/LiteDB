using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Implement update command to a document inside a collection. Return number of documents updated
        /// </summary>
        public int Update(string collection, IEnumerable<BsonDocument> docs)
        {
            if (collection.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(collection));
            if (docs == null) throw new ArgumentNullException(nameof(docs));

            return this.AutoTransaction(transaction =>
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Write, collection, false);
                var collectionPage = snapshot.CollectionPage;
                var indexer = new IndexService(snapshot, _header.Pragmas.Collation, _disk.MAX_ITEMS_COUNT);
                var data = new DataService(snapshot, _disk.MAX_ITEMS_COUNT);
                var vectorService = new VectorIndexService(snapshot, _header.Pragmas.Collation);
                var count = 0;

                if (collectionPage == null) return 0;

                LOG($"update `{collection}`", "COMMAND");

                foreach (var doc in docs)
                {
                    _state.Validate();

                    transaction.Safepoint();

                    if (this.UpdateDocument(snapshot, collectionPage, doc, indexer, data, vectorService, true))
                    {
                        count++;
                    }
                }

                return count;
            });
        }

        /// <summary>
        /// Update documents using transform expression (must return a scalar/document value) using predicate as filter
        /// </summary>
        public int UpdateMany(string collection, BsonExpression transform, BsonExpression predicate)
        {
            if (collection.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(collection));
            if (transform == null) throw new ArgumentNullException(nameof(transform));

            return this.Update(collection, transformDocs());

            IEnumerable<BsonDocument> transformDocs()
            {
                var q = new Query { Select = "$", ForUpdate = true };

                if (predicate != null)
                {
                    q.Where.Add(predicate);
                }

                using (var reader = this.Query(collection, q))
                {
                    while (reader.Read())
                    {
                        var doc = reader.Current.AsDocument;

                        var id = doc["_id"];
                        var value = transform.ExecuteScalar(doc, _header.Pragmas.Collation);

                        if (!value.IsDocument) throw new ArgumentException("Extend expression must return a document", nameof(transform));

                        var result = BsonExpressionMethods.EXTEND(doc, value.AsDocument).AsDocument;

                        // be sure result document will contain same _id as current doc
                        if (result.TryGetValue("_id", out var newId))
                        {
                            if (newId != id) throw LiteException.InvalidUpdateField("_id");
                        }
                        else
                        {
                            result["_id"] = id;
                        }

                        yield return result;
                    }
                }
            }
        }

        /// <summary>
        /// Implement internal update document
        /// </summary>
        private bool UpdateDocument(Snapshot snapshot, CollectionPage col, BsonDocument doc, IndexService indexer, DataService data, VectorIndexService vectorService, bool rejectInvalidLocalTime = false)
        {
            // normalize id before find
            var id = doc["_id"];
            
            // validate id for null, min/max values
            if (id.IsNull || id.IsMinValue || id.IsMaxValue)
            {
                throw LiteException.InvalidDataType("_id", id);
            }
            
            // find indexNode from pk index
            var pkNode = indexer.Find(col.PK, id, false, LiteDB.Query.Ascending);
            
            // if not found document, no updates
            if (pkNode == null) return false;

            // Upsert already checked the document; Update only checks what it is about to write
            if (rejectInvalidLocalTime) this.RejectInvalidLocalTime(doc);

            // update data storage
            data.Update(col, pkNode.DataBlock, doc);
            foreach (var (vectorIndex, metadata) in col.GetVectorIndexes())
            {
                vectorService.Upsert(vectorIndex, metadata, doc, pkNode.DataBlock);
            }
            
            // Snapshot values/addresses, never page-backed nodes: deletion can reuse their slots.
            var oldKeys = new UpdateIndexKeys();
            foreach (var node in indexer.GetNodeList(pkNode.NextNode))
            {
                oldKeys.Add(node.Slot, node.Key, node.Position, null);
            }
            var newKeys = new UpdateIndexKeys();

            foreach (var index in col.GetCollectionIndexes())
            {
                if (index.Name == "_id" || index.IndexType != 0) continue;

                foreach (var key in index.BsonExpr.GetIndexKeys(doc, _header.Pragmas.Collation))
                {
                    newKeys.Add(index.Slot, key, PageAddress.Empty, index.Name);
                }
            }

            HashSet<PageAddress> toDelete = null;
            for (var i = 0; i < oldKeys.Count; i++)
            {
                var old = oldKeys[i];
                if (newKeys.Contains(old.Slot, old.Key)) continue;
                if (toDelete == null) toDelete = new HashSet<PageAddress>();
                toDelete.Add(old.Position);
            }

            // Keep the original membership comparison even for collation-equivalent
            // keys. Expression evaluation and index ordering still use the collation.
            var toInsert = new UpdateIndexKeys();
            for (var i = 0; i < newKeys.Count; i++)
            {
                var key = newKeys[i];
                if (!oldKeys.Contains(key.Slot, key.Key))
                {
                    toInsert.Add(key.Slot, key.Key, key.Position, key.Name);
                }
            }

            if (toDelete == null && toInsert.Count == 0) return true;

            // Delete before inserting, preserving unique-key checks and linked-list order.
            var last = indexer.DeleteList(pkNode.Position, toDelete);
            for (var i = 0; i < toInsert.Count; i++)
            {
                var key = toInsert[i];
                last = indexer.AddNode(col.GetCollectionIndex(key.Name), key.Key, pkNode.DataBlock, last);
            }

            return true;
        }
    }
}
