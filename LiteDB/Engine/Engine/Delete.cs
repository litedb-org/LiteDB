﻿using System;
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
                var data = new DataService(snapshot, _disk.MAX_ITEMS_COUNT);
                var indexer = new IndexService(snapshot, _header.Pragmas.Collation, _disk.MAX_ITEMS_COUNT);
                var vectorService = new VectorIndexService(snapshot, _header.Pragmas.Collation);

                if (collectionPage == null) return 0;

                LOG($"delete `{collection}`", "COMMAND");

                var count = 0;
                var pk = collectionPage.PK;

                foreach (var id in ids)
                {
                    var pkNode = indexer.Find(pk, id, false, LiteDB.Query.Ascending);

                    // if pk not found, continue
                    if (pkNode == null)
                    {
                        transaction.Safepoint();
                        continue;
                    }

                    _state.Validate();

                    foreach (var (_, metadata) in collectionPage.GetVectorIndexes())
                    {
                        vectorService.Delete(metadata, pkNode.DataBlock);
                    }

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

            // Shared coordinated reads keep the established query/snapshot ownership path.
            if (!_settings.SharedMode && !_settings.DurableCommits && _header.Pragmas.IndexesOrdered && _settings.ReadTransform == null && !collection.StartsWith("$") &&
                PrimaryKeyDeleteRange.HasShape(predicate))
            {
                return this.DeleteRange(collection, predicate);
            }

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
                return this.DeleteManyViaQuery(collection, predicate);
            }
        }

        private int DeleteManyViaQuery(string collection, BsonExpression predicate)
        {
            IEnumerable<BsonValue> getIds()
            {
                // this is intresting: if _id returns an document (like in FileStorage) you can't run direct _id
                // field because "reader.Current" will return _id document - but not - { _id: [document] }
                // create inner document to ensure _id will be a document
                var query = new Query { Select = "{ i: _id }", ForUpdate = true };

                if(predicate != null)
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
                            yield return value;
                        }
                    }
                }
            }

            return this.Delete(collection, getIds());
        }

        private int DeleteRange(string collection, BsonExpression predicate)
        {
            return this.AutoTransaction(transaction =>
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Write, collection, false);
                var page = snapshot.CollectionPage;
                if (page == null) return 0;
                // Evaluate bindings only after admission and collection lookup, just
                // as the query path does (including its missing-collection behavior).
                if (!PrimaryKeyDeleteRange.TryCreate(predicate, _header.Pragmas.Collation, out var range))
                    return this.DeleteManyViaQuery(collection, predicate);
                var data = new DataService(snapshot, _disk.MAX_ITEMS_COUNT);
                var indexer = new IndexService(snapshot, _header.Pragmas.Collation, _disk.MAX_ITEMS_COUNT);
                var vectors = new VectorIndexService(snapshot, _header.Pragmas.Collation);
                var count = 0;
                foreach (var position in range.DeletePositions(indexer, page.PK))
                {
                    _state.Validate();
                    var node = indexer.GetNode(position);
                    foreach (var (_, metadata) in page.GetVectorIndexes())
                    {
                        vectors.Delete(metadata, node.DataBlock);
                    }
                    data.Delete(node.DataBlock);
                    indexer.DeleteAll(position);
                    transaction.Safepoint();
                    count++;
                }
                return count;
            });
        }
    }
}
