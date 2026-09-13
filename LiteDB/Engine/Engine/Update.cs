using System;
using System.Collections.Generic;
using System.Linq;
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
                var count = 0;

                if (collectionPage == null) return 0;

                LOG($"update `{collection}`", "COMMAND");

                foreach (var doc in docs)
                {
                    _state.Validate();

                    transaction.Safepoint();

                    if (this.UpdateDocument(snapshot, collectionPage, doc, indexer, data))
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
        private bool UpdateDocument(Snapshot snapshot, CollectionPage col, BsonDocument doc, IndexService indexer, DataService data)
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
            
            // update data storage
            data.Update(col, pkNode.DataBlock, doc);
            
            // get all current non-pk index nodes from this data block (slot, key, nodePosition)
            var oldKeys = indexer.GetNodeList(pkNode.NextNode)
                .Select(x => new Tuple<byte, BsonValue, PageAddress>(x.Slot, x.Key, x.Position))
                .ToArray();

            // build a list of all new key index keys
            var newKeys = new List<Tuple<byte, BsonValue, string>>();

            foreach (var index in col.GetCollectionIndexes().Where(x => x.Name != "_id"))
            {
                // getting all keys from expression over document
                var keys = index.BsonExpr.GetIndexKeys(doc, _header.Pragmas.Collation);

                foreach (var key in keys)
                {
                    newKeys.Add(new Tuple<byte, BsonValue, string>(index.Slot, key, index.Name));
                }
            }

            if (oldKeys.Length == 0 && newKeys.Count == 0) return true;

            // get a list of all nodes that are in oldKeys but not in newKeys (must delete)
            var toDelete = new HashSet<PageAddress>(oldKeys
                .Where(x => newKeys.Any(n => n.Item1 == x.Item1 && n.Item2 == x.Item2) == false)
                .Select(x => x.Item3));

            // get a list of all keys that are not in oldKeys (must insert)
            var toInsert = newKeys
                .Where(x => oldKeys.Any(o => o.Item1 == x.Item1 && o.Item2 == x.Item2) == false)
                .ToArray();

            // if nothing to change, just exit
            if (toDelete.Count == 0 && toInsert.Length == 0) return true;

            // delete nodes and return last keeped node in list
            var last = indexer.DeleteList(pkNode.Position, toDelete);

            // now, insert all new nodes
            foreach(var elem in toInsert)
            {
                var index = col.GetCollectionIndex(elem.Item3);

                last = indexer.AddNode(index, elem.Item2, pkNode.DataBlock, last);
            }

            return true;
        }
    }
}