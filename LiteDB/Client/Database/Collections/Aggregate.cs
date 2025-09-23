using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        #region Count

        /// <summary>
        /// Get document count in collection
        /// </summary>
        public Task<int> CountAsync(CancellationToken cancellationToken = default)
        {
            return this.Query().CountAsync(cancellationToken);
        }

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public Task<int> CountAsync(BsonExpression predicate, CancellationToken cancellationToken = default)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            return this.Query().Where(predicate).CountAsync(cancellationToken);
        }

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public Task<int> CountAsync(string predicate, BsonDocument parameters, CancellationToken cancellationToken = default)
        {
            return this.CountAsync(BsonExpression.Create(predicate, parameters), cancellationToken);
        }

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public Task<int> CountAsync(string predicate, CancellationToken cancellationToken = default, params BsonValue[] args)
        {
            return this.CountAsync(BsonExpression.Create(predicate, args), cancellationToken);
        }

        /// <summary>
        /// Count documents matching a query. This method does not deserialize any documents. Needs indexes on query expression
        /// </summary>
        public Task<int> CountAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return this.CountAsync(_mapper.GetExpression(predicate), cancellationToken);
        }

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public Task<int> CountAsync(Query query, CancellationToken cancellationToken = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));

            return new LiteQueryable<T>(_engine, _mapper, _collection, query).CountAsync(cancellationToken);
        }

        #endregion

        #region LongCount

        /// <summary>
        /// Get document count in collection
        /// </summary>
        public Task<long> LongCountAsync(CancellationToken cancellationToken = default)
        {
            return this.Query().LongCountAsync(cancellationToken);
        }

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public Task<long> LongCountAsync(BsonExpression predicate, CancellationToken cancellationToken = default)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            return this.Query().Where(predicate).LongCountAsync(cancellationToken);
        }

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public Task<long> LongCountAsync(string predicate, BsonDocument parameters, CancellationToken cancellationToken = default)
        {
            return this.LongCountAsync(BsonExpression.Create(predicate, parameters), cancellationToken);
        }

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public Task<long> LongCountAsync(string predicate, CancellationToken cancellationToken = default, params BsonValue[] args)
        {
            return this.LongCountAsync(BsonExpression.Create(predicate, args), cancellationToken);
        }

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public Task<long> LongCountAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return this.LongCountAsync(_mapper.GetExpression(predicate), cancellationToken);
        }

        /// <summary>
        /// Get document count in collection using predicate filter expression
        /// </summary>
        public Task<long> LongCountAsync(Query query, CancellationToken cancellationToken = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));

            return new LiteQueryable<T>(_engine, _mapper, _collection, query).LongCountAsync(cancellationToken);
        }

        #endregion

        #region Exists

        /// <summary>
        /// Get true if collection contains at least 1 document that satisfies the predicate expression
        /// </summary>
        public Task<bool> ExistsAsync(BsonExpression predicate, CancellationToken cancellationToken = default)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            return this.Query().Where(predicate).ExistsAsync(cancellationToken);
        }

        /// <summary>
        /// Get true if collection contains at least 1 document that satisfies the predicate expression
        /// </summary>
        public Task<bool> ExistsAsync(string predicate, BsonDocument parameters, CancellationToken cancellationToken = default)
        {
            return this.ExistsAsync(BsonExpression.Create(predicate, parameters), cancellationToken);
        }

        /// <summary>
        /// Get true if collection contains at least 1 document that satisfies the predicate expression
        /// </summary>
        public Task<bool> ExistsAsync(string predicate, CancellationToken cancellationToken = default, params BsonValue[] args)
        {
            return this.ExistsAsync(BsonExpression.Create(predicate, args), cancellationToken);
        }

        /// <summary>
        /// Get true if collection contains at least 1 document that satisfies the predicate expression
        /// </summary>
        public Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return this.ExistsAsync(_mapper.GetExpression(predicate), cancellationToken);
        }

        /// <summary>
        /// Get true if collection contains at least 1 document that satisfies the predicate expression
        /// </summary>
        public Task<bool> ExistsAsync(Query query, CancellationToken cancellationToken = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));

            return new LiteQueryable<T>(_engine, _mapper, _collection, query).ExistsAsync(cancellationToken);
        }

        #endregion

        #region Min/Max

        /// <summary>
        /// Returns the min value from specified key value in collection
        /// </summary>
        public async Task<BsonValue> MinAsync(BsonExpression keySelector, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(keySelector)) throw new ArgumentNullException(nameof(keySelector));

            await foreach (var doc in this.Query()
                .OrderBy(keySelector)
                .Select(keySelector)
                .ToDocumentsAsync(cancellationToken)
                .WithCancellation(cancellationToken))
            {
                return doc[doc.Keys.First()];
            }

            throw new InvalidOperationException("Sequence contains no elements.");
        }

        /// <summary>
        /// Returns the min value of _id index
        /// </summary>
        public Task<BsonValue> MinAsync(CancellationToken cancellationToken = default)
        {
            return this.MinAsync("_id", cancellationToken);
        }

        /// <summary>
        /// Returns the min value from specified key value in collection
        /// </summary>
        public async Task<K> MinAsync<K>(Expression<Func<T, K>> keySelector, CancellationToken cancellationToken = default)
        {
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

            var expr = _mapper.GetExpression(keySelector);
            var value = await this.MinAsync(expr, cancellationToken).ConfigureAwait(false);

            return (K)_mapper.Deserialize(typeof(K), value);
        }

        /// <summary>
        /// Returns the max value from specified key value in collection
        /// </summary>
        public async Task<BsonValue> MaxAsync(BsonExpression keySelector, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(keySelector)) throw new ArgumentNullException(nameof(keySelector));

            await foreach (var doc in this.Query()
                .OrderByDescending(keySelector)
                .Select(keySelector)
                .ToDocumentsAsync(cancellationToken)
                .WithCancellation(cancellationToken))
            {
                return doc[doc.Keys.First()];
            }

            throw new InvalidOperationException("Sequence contains no elements.");
        }

        /// <summary>
        /// Returns the max _id index key value
        /// </summary>
        public Task<BsonValue> MaxAsync(CancellationToken cancellationToken = default)
        {
            return this.MaxAsync("_id", cancellationToken);
        }

        /// <summary>
        /// Returns the last/max field using a linq expression
        /// </summary>
        public async Task<K> MaxAsync<K>(Expression<Func<T, K>> keySelector, CancellationToken cancellationToken = default)
        {
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

            var expr = _mapper.GetExpression(keySelector);
            var value = await this.MaxAsync(expr, cancellationToken).ConfigureAwait(false);

            return (K)_mapper.Deserialize(typeof(K), value);
        }

        #endregion
    }
}
