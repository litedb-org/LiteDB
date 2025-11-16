using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <inheritdoc/>
        public bool Update(T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            // get BsonDocument from object
            var doc = _mapper.ToDocument(entity);

            return _engine.Update(_collection, new BsonDocument[] { doc }) > 0;
        }

        /// <inheritdoc/>
        public bool Update(BsonValue id, T entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));

            // get BsonDocument from object
            var doc = _mapper.ToDocument(entity);

            // set document _id using id parameter
            doc["_id"] = id;

            return _engine.Update(_collection, new BsonDocument[] { doc }) > 0;
        }

        /// <inheritdoc/>
        public int Update(IEnumerable<T> entities)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            return _engine.Update(_collection, entities.Select(x => _mapper.ToDocument(x)));
        }

        /// <inheritdoc/>
        public int UpdateMany(BsonExpression transform, BsonExpression predicate)
        {
            if (transform == null) throw new ArgumentNullException(nameof(transform));
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            if (transform.Type != BsonExpressionType.Document)
            {
                throw new ArgumentException("Extend expression must return a document. Eg: `col.UpdateMany('{ Name: UPPER(Name) }', 'Age > 10')`");
            }

            return _engine.UpdateMany(_collection, transform, predicate);
        }

        /// <inheritdoc/>
        public int UpdateMany(Expression<Func<T, T>> extend, Expression<Func<T, bool>> predicate)
        {
            if (extend == null) throw new ArgumentNullException(nameof(extend));
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            var ext = _mapper.GetExpression(extend);
            var pred = _mapper.GetExpression(predicate);

            if (ext.Type != BsonExpressionType.Document)
            {
                throw new ArgumentException("Extend expression must return an anonymous class to be merge with entities. Eg: `col.UpdateMany(x => new { Name = x.Name.ToUpper() }, x => x.Age > 10)`");
            }

            return _engine.UpdateMany(_collection, ext, pred);
        }
    }
}