using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <inheritdoc/>
        public bool Upsert(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            return this.Upsert(new T[] { entity }) == 1;
        }

        /// <inheritdoc/>
        public int Upsert(IEnumerable<T> entities)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            return _engine.Upsert(_collection, this.GetBsonDocs(entities), _autoId);
        }

        /// <inheritdoc/>
        public bool Upsert(BsonValue id, T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));

            // get BsonDocument from object
            var doc = _mapper.ToDocument(entity);

            // set document _id using id parameter
            doc["_id"] = id;

            return _engine.Upsert(_collection, new[] { doc }, _autoId) > 0;
        }
    }
}