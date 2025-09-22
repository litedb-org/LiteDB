using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <summary>
        /// Insert or Update a document in this collection.
        /// </summary>
        public async Task<bool> UpsertAsync(T entity, CancellationToken cancellationToken = default)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            cancellationToken.ThrowIfCancellationRequested();

            var count = await this.UpsertAsync(new T[] { entity }, cancellationToken).ConfigureAwait(false);

            return count == 1;
        }

        /// <summary>
        /// Insert or Update all documents
        /// </summary>
        public Task<int> UpsertAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            var result = _engine.Upsert(_collection, this.GetBsonDocs(entities, cancellationToken), _autoId);

            return Task.FromResult(result);
        }

        /// <summary>
        /// Insert or Update a document in this collection.
        /// </summary>
        public Task<bool> UpsertAsync(BsonValue id, T entity, CancellationToken cancellationToken = default)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));
            cancellationToken.ThrowIfCancellationRequested();

            // get BsonDocument from object
            var doc = _mapper.ToDocument(entity);

            // set document _id using id parameter
            doc["_id"] = id;

            var result = _engine.Upsert(_collection, new[] { doc }, _autoId) > 0;

            return Task.FromResult(result);
        }
    }
}