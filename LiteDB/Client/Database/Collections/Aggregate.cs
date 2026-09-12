using System;
using System.Linq;
using System.Linq.Expressions;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        #region Count

        /// <summary>
        /// Get document count in collection
        /// </summary>
        public int Count()
        {
            // do not use indexes - collections has DocumentCount property
            return this.Query().Count();
        }

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public int Count(BsonExpression predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            return this.Query().Where(predicate).Count();
        }

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public int Count(string predicate, BsonDocument parameters) => this.Count(BsonExpression.Create(predicate, parameters));

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public int Count(string predicate, params BsonValue[] args) => this.Count(BsonExpression.Create(predicate, args));

        /// <summary>
        /// Count documents matching a query. This method does not deserialize any documents. Needs indexes on query expression
        /// </summary>
        public int Count(Expression<Func<T, bool>> predicate) => this.Count(_mapper.GetExpression(predicate));

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public int Count(Query query) => new LiteQueryable<T>(_engine, _mapper, _collection, query).Count();

        #endregion

        #region LongCount

        /// <summary>
        /// Get document count in collection
        /// </summary>
        public long LongCount()
        {
            return this.Query().LongCount();
        }

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public long LongCount(BsonExpression predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            return this.Query().Where(predicate).LongCount();
        }

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public long LongCount(string predicate, BsonDocument parameters) => this.LongCount(BsonExpression.Create(predicate, parameters));

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public long LongCount(string predicate, params BsonValue[] args) => this.LongCount(BsonExpression.Create(predicate, args));

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public long LongCount(Expression<Func<T, bool>> predicate) => this.LongCount(_mapper.GetExpression(predicate));

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public long LongCount(Query query) => new LiteQueryable<T>(_engine, _mapper, _collection, query).Count();

        #endregion

        #region Exists

        /// <summary>
        /// Get true if collection contains at least 1 document that satisfies the predicate expression
        /// </summary>
        public bool Exists(BsonExpression predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            return this.Query().Where(predicate).Exists();
        }

        /// <summary>
        /// Get true if collection contains at least 1 document that satisfies the predicate expression
        /// </summary>
        public bool Exists(string predicate, BsonDocument parameters) => this.Exists(BsonExpression.Create(predicate, parameters));

        /// <summary>
        /// Get true if collection contains at least 1 document that satisfies the predicate expression
        /// </summary>
        public bool Exists(string predicate, params BsonValue[] args) => this.Exists(BsonExpression.Create(predicate, args));

        /// <summary>
        /// Get true if collection contains at least 1 document that satisfies the predicate expression
        /// </summary>
        public bool Exists(Expression<Func<T, bool>> predicate) => this.Exists(_mapper.GetExpression(predicate));

        /// <summary>
        /// Get true if collection contains at least 1 document that satisfies the predicate expression
        /// </summary>
        public bool Exists(Query query) => new LiteQueryable<T>(_engine, _mapper, _collection, query).Exists();

        #endregion

        #region Min/Max

        /// <summary>
        /// Returns the min value from specified key value in collection
        /// </summary>
        public BsonValue Min(BsonExpression keySelector)
        {
            if (string.IsNullOrEmpty(keySelector)) throw new ArgumentNullException(nameof(keySelector));

            return this.TryGetAggregateValue(keySelector, false, out var value)
                ? value
                : BsonValue.Null;
        }

        /// <summary>
        /// Returns the min value of _id index
        /// </summary>
        public BsonValue Min() => this.Min("_id");

        /// <summary>
        /// Returns the min value from specified key value in collection
        /// </summary>
        public K Min<K>(Expression<Func<T, K>> keySelector)
        {
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

            var expr = _mapper.GetExpression(keySelector);

            var hasValue = this.TryGetAggregateValue(expr, false, out var value);

            var result = _mapper.Deserialize(typeof(K), value);

            if (hasValue == false && result == null) return default(K);

            // A stored BSON null is different from an empty result. Let the
            // cast fail for an incompatible non-nullable K instead of inventing 0.
            return (K)result;
        }

        /// <summary>
        /// Returns the max value from specified key value in collection
        /// </summary>
        public BsonValue Max(BsonExpression keySelector)
        {
            if (string.IsNullOrEmpty(keySelector)) throw new ArgumentNullException(nameof(keySelector));

            return this.TryGetAggregateValue(keySelector, true, out var value)
                ? value
                : BsonValue.Null;
        }

        /// <summary>
        /// Returns the max _id index key value
        /// </summary>
        public BsonValue Max() => this.Max("_id");

        /// <summary>
        /// Returns the last/max field using a linq expression
        /// </summary>
        public K Max<K>(Expression<Func<T, K>> keySelector)
        {
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

            var expr = _mapper.GetExpression(keySelector);

            var hasValue = this.TryGetAggregateValue(expr, true, out var value);

            var result = _mapper.Deserialize(typeof(K), value);

            if (hasValue == false && result == null) return default(K);

            return (K)result;
        }

        private bool TryGetAggregateValue(BsonExpression keySelector, bool descending, out BsonValue value)
        {
            var query = descending
                ? this.Query().OrderByDescending(keySelector)
                : this.Query().OrderBy(keySelector);
            var doc = query
                .Select(keySelector)
                .ToDocuments()
                .FirstOrDefault();

            if (doc == null)
            {
                value = BsonValue.Null;
                return false;
            }

            value = doc[doc.Keys.First()];
            return true;
        }

        #endregion
    }
}
