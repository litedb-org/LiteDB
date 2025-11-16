using System;
using System.Linq;
using System.Linq.Expressions;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        #region Count

        /// <inheritdoc/>
        public int Count()
        {
            // do not use indexes - collections has DocumentCount property
            return this.Query().Count();
        }

        /// <inheritdoc/>
        public int Count(BsonExpression predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            return this.Query().Where(predicate).Count();
        }

        /// <inheritdoc/>
        public int Count(string predicate, BsonDocument parameters) => this.Count(BsonExpression.Create(predicate, parameters));

        /// <inheritdoc/>
        public int Count(string predicate, params BsonValue[] args) => this.Count(BsonExpression.Create(predicate, args));

        /// <inheritdoc/>
        public int Count(Expression<Func<T, bool>> predicate) => this.Count(_mapper.GetExpression(predicate));

        /// <inheritdoc/>
        public int Count(Query query) => new LiteQueryable<T>(_engine, _mapper, _collection, query).Count();

        #endregion

        #region LongCount

        /// <inheritdoc/>
        public long LongCount()
        {
            return this.Query().LongCount();
        }

        /// <inheritdoc/>
        public long LongCount(BsonExpression predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            return this.Query().Where(predicate).LongCount();
        }

        /// <inheritdoc/>
        public long LongCount(string predicate, BsonDocument parameters) => this.LongCount(BsonExpression.Create(predicate, parameters));

        /// <inheritdoc/>
        public long LongCount(string predicate, params BsonValue[] args) => this.LongCount(BsonExpression.Create(predicate, args));

        /// <inheritdoc/>
        public long LongCount(Expression<Func<T, bool>> predicate) => this.LongCount(_mapper.GetExpression(predicate));

        /// <inheritdoc/>
        public long LongCount(Query query) => new LiteQueryable<T>(_engine, _mapper, _collection, query).Count();

        #endregion

        #region Exists

        /// <inheritdoc/>
        public bool Exists(BsonExpression predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            return this.Query().Where(predicate).Exists();
        }

        /// <inheritdoc/>
        public bool Exists(string predicate, BsonDocument parameters) => this.Exists(BsonExpression.Create(predicate, parameters));

        /// <inheritdoc/>
        public bool Exists(string predicate, params BsonValue[] args) => this.Exists(BsonExpression.Create(predicate, args));

        /// <inheritdoc/>
        public bool Exists(Expression<Func<T, bool>> predicate) => this.Exists(_mapper.GetExpression(predicate));

        /// <inheritdoc/>
        public bool Exists(Query query) => new LiteQueryable<T>(_engine, _mapper, _collection, query).Exists();

        #endregion

        #region Min/Max

        /// <inheritdoc/>
        public BsonValue Min(BsonExpression keySelector)
        {
            if (string.IsNullOrEmpty(keySelector)) throw new ArgumentNullException(nameof(keySelector));

            var doc = this.Query()
                .OrderBy(keySelector)
                .Select(keySelector)
                .ToDocuments()
                .First();

            // return first field of first document
            return doc[doc.Keys.First()];
        }

        /// <inheritdoc/>
        public BsonValue Min() => this.Min("_id");

        /// <inheritdoc/>
        public K Min<K>(Expression<Func<T, K>> keySelector)
        {
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

            var expr = _mapper.GetExpression(keySelector);

            var value = this.Min(expr);

            return (K)_mapper.Deserialize(typeof(K), value);
        }

        /// <inheritdoc/>
        public BsonValue Max(BsonExpression keySelector)
        {
            if (string.IsNullOrEmpty(keySelector)) throw new ArgumentNullException(nameof(keySelector));

            var doc = this.Query()
                .OrderByDescending(keySelector)
                .Select(keySelector)
                .ToDocuments()
                .First();

            // return first field of first document
            return doc[doc.Keys.First()];
        }

        /// <inheritdoc/>
        public BsonValue Max() => this.Max("_id");

        /// <inheritdoc/>
        public K Max<K>(Expression<Func<T, K>> keySelector)
        {
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

            var expr = _mapper.GetExpression(keySelector);

            var value = this.Max(expr);

            return (K)_mapper.Deserialize(typeof(K), value);
        }

        #endregion
    }
}