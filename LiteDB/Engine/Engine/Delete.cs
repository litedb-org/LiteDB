using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Implements delete based on IDs enumerable
        /// </summary>
        public int Delete(string collection, IEnumerable<BsonValue> ids)
        {
            if (collection.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(collection));
            if (ids == null) throw new ArgumentNullException(nameof(ids));

            return this.AutoTransaction(transaction =>
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Write, collection, false);
                var collectionPage = snapshot.CollectionPage;
                var data = new DataService(snapshot, () => _disk.MAX_ITEMS_COUNT);
                var indexer = new IndexService(snapshot, _header.Pragmas.Collation, () => _disk.MAX_ITEMS_COUNT);

                if (collectionPage == null) return 0;

                LOG($"delete `{collection}`", "COMMAND");

                var count = 0;
                var pk = collectionPage.PK;

                foreach (var id in ids)
                {
                    var pkNode = indexer.Find(pk, id, false, LiteDB.Query.Ascending);

                    // if pk not found, continue
                    if (pkNode == null) continue;

                    _state.Validate();

                    // remove object data
                    data.Delete(pkNode.DataBlock);

                    // delete all nodes (start in pk node)
                    indexer.DeleteAll(pkNode.Position);

                    transaction.Safepoint();

                    count++;
                }

                return count;
            });
        }

        /// <summary>
        /// Implements delete based on filter expression
        /// </summary>
        public int DeleteMany(string collection, BsonExpression predicate)
        {
            if (collection.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(collection));

            // do optimization for when using "_id = value" key
            if (predicate != null &&
                predicate.Type == BsonExpressionType.Equal && 
                predicate.Left.Type == BsonExpressionType.Path && 
                predicate.Left.Source == "$._id" && 
                predicate.Right.IsValue)
            {
                var id = predicate.Right.Execute(_header.Pragmas.Collation).First();

                return this.Delete(collection, new BsonValue[] { id });
            }
            else
            {
                // Delete documents in small batches to avoid creating massive WAL files when
                // removing large result sets with a single transaction.
                var deleted = 0;
                var ids = new List<BsonValue>(512);

                while (true)
                {
                    ids.Clear();

                    // this is interesting: if _id returns a document (like in FileStorage) you can't run direct _id
                    // field because "reader.Current" will return _id document - but not - { _id: [document] }
                    // create inner document to ensure _id will be a document
                    var query = new Query { Select = "{ i: _id }", ForUpdate = true, Limit = 2048 };

                    if (predicate != null)
                    {
                        query.Where.Add(predicate);
                    }

                    using (var reader = this.Query(collection, query))
                    {
                        while (reader.Read())
                        {
                            var value = reader.Current["i"];

                            if (value != BsonValue.Null)
                            {
                                ids.Add(value);
                            }
                        }
                    }

                    if (ids.Count == 0) break;

                    deleted += this.Delete(collection, ids.ToArray());

                    // continue looping until there are no more documents matching the predicate
                }

                return deleted;
            }
        }
    }
}