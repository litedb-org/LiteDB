using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <summary>
        /// Update a document in this collection. Returns false if not found document in collection
        /// </summary>
        public Task<bool> UpdateAsync(T entity, CancellationToken cancellationToken = default)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            cancellationToken.ThrowIfCancellationRequested();

            var doc = _mapper.ToDocument(entity);

            var result = _engine.Update(_collection, new BsonDocument[] { doc }) > 0;

            return Task.FromResult(result);
        }

        /// <summary>
        /// Update a document in this collection. Returns false if not found document in collection
        /// </summary>
        public Task<bool> UpdateAsync(BsonValue id, T entity, CancellationToken cancellationToken = default)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));
            cancellationToken.ThrowIfCancellationRequested();

            var doc = _mapper.ToDocument(entity);

            doc["_id"] = id;

            var result = _engine.Update(_collection, new BsonDocument[] { doc }) > 0;

            return Task.FromResult(result);
        }

        /// <summary>
        /// Update all documents
        /// </summary>
        public Task<int> UpdateAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            var docs = new List<BsonDocument>();

            foreach (var entity in entities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                docs.Add(_mapper.ToDocument(entity));
            }

            var result = _engine.Update(_collection, docs);

            return Task.FromResult(result);
        }

        /// <summary>
        /// Update many documents based on transform expression. This expression must return a new document that will be replaced over current document (according with predicate).
        /// Eg: col.UpdateMany("{ Name: UPPER($.Name), Age }", "_id > 0")
        /// </summary>
        public Task<int> UpdateManyAsync(BsonExpression transform, BsonExpression predicate, CancellationToken cancellationToken = default)
        {
            if (transform == null) throw new ArgumentNullException(nameof(transform));
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            cancellationToken.ThrowIfCancellationRequested();

            if (transform.Type != BsonExpressionType.Document)
            {
                throw new ArgumentException("Extend expression must return a document. Eg: `col.UpdateMany('{ Name: UPPER(Name)}', 'Age > 10')`");
            }

            var result = _engine.UpdateMany(_collection, transform, predicate);

            return Task.FromResult(result);
        }

        /// <summary>
        /// Update many document based on merge current document with extend expression. Use your class with initializers.
        /// Eg: col.UpdateMany(x => new Customer { Name = x.Name.ToUpper(), Salary: 100 }, x => x.Name == "John")
        /// </summary>
        public Task<int> UpdateManyAsync(Expression<Func<T, T>> extend, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
        {
            if (extend == null) throw new ArgumentNullException(nameof(extend));
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            cancellationToken.ThrowIfCancellationRequested();

            var ext = _mapper.GetExpression(extend);
            var pred = _mapper.GetExpression(predicate);

            if (ext.Type != BsonExpressionType.Document)
            {
                throw new ArgumentException("Extend expression must return an anonymous class to be merge with entities. Eg: `col.UpdateMany(x => new { Name = x.Name.ToUpper() }, x => x.Age > 10)`");
            }

            var result = _engine.UpdateMany(_collection, ext, pred);

            return Task.FromResult(result);
        }
    }
}
