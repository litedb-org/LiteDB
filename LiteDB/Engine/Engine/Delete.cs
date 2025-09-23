using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Implements delete based on IDs enumerable
        /// </summary>
        public Task<int> DeleteAsync(string collection, IEnumerable<BsonValue> ids, CancellationToken cancellationToken = default)
        {
            if (collection.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(collection));
            if (ids == null) throw new ArgumentNullException(nameof(ids));

            cancellationToken.ThrowIfCancellationRequested();

            return this.AutoTransactionAsync((transaction, token) =>
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Write, collection, false);
                var collectionPage = snapshot.CollectionPage;
                var data = new DataService(snapshot, _disk.MAX_ITEMS_COUNT);
                var indexer = new IndexService(snapshot, _header.Pragmas.Collation, _disk.MAX_ITEMS_COUNT);

                if (collectionPage == null) return new ValueTask<int>(0);

                LOG($"delete `{collection}`", "COMMAND");

                var count = 0;
                var pk = collectionPage.PK;

                foreach (var id in ids)
                {
                    token.ThrowIfCancellationRequested();

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

                return new ValueTask<int>(count);
            }, cancellationToken);
        }

        /// <summary>
        /// Implements delete based on filter expression
        /// </summary>
        public async Task<int> DeleteManyAsync(string collection, BsonExpression predicate, CancellationToken cancellationToken = default)
        {
            if (collection.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(collection));
            cancellationToken.ThrowIfCancellationRequested();

            // do optimization for when using "_id = value" key
            if (predicate != null &&
                predicate.Type == BsonExpressionType.Equal &&
                predicate.Left.Type == BsonExpressionType.Path &&
                predicate.Left.Source == "$._id" &&
                predicate.Right.IsValue)
            {
                var id = predicate.Right.Execute(_header.Pragmas.Collation).First();

                return await this.DeleteAsync(collection, new BsonValue[] { id }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // this is interesting: if _id returns a document (like in FileStorage) you can't run direct _id
                // field because "reader.Current" will return _id document - but not - { _id: [document] }
                // create inner document to ensure _id will be a document
                var query = new Query { Select = "{ i: _id }", ForUpdate = true };

                if (predicate != null)
                {
                    query.Where.Add(predicate);
                }

                var ids = new List<BsonValue>();

                await using (var reader = await this.QueryAsync(collection, query, cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var value = reader.Current["i"];

                        if (value != BsonValue.Null)
                        {
                            ids.Add(value);
                        }
                    }
                }

                return await this.DeleteAsync(collection, ids, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}