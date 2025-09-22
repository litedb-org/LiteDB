using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using static LiteDB.Constants;

namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <summary>
        /// Return a new LiteQueryable to build more complex queries
        /// </summary>
        public ILiteQueryable<T> Query()
        {
            return new LiteQueryable<T>(_engine, _mapper, _collection, new Query()).Include(_includes);
        }

        #region Find

        /// <summary>
        /// Find documents inside a collection using predicate expression.
        /// </summary>
        public IAsyncEnumerable<T> FindAsync(BsonExpression predicate, int skip = 0, int limit = int.MaxValue, CancellationToken cancellationToken = default)
        {
            return ToAsyncEnumerable(this.FindSync(predicate, skip, limit), cancellationToken);
        }

        /// <summary>
        /// Find documents inside a collection using query definition.
        /// </summary>
        public IAsyncEnumerable<T> FindAsync(Query query, int skip = 0, int limit = int.MaxValue, CancellationToken cancellationToken = default)
        {
            return ToAsyncEnumerable(this.FindSync(query, skip, limit), cancellationToken);
        }

        /// <summary>
        /// Find documents inside a collection using predicate expression.
        /// </summary>
        public IAsyncEnumerable<T> FindAsync(Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue, CancellationToken cancellationToken = default)
        {
            return ToAsyncEnumerable(this.FindSync(predicate, skip, limit), cancellationToken);
        }

        #endregion

        #region FindById + One + All

        /// <summary>
        /// Find a document using Document Id. Returns null if not found.
        /// </summary>
        public Task<T> FindByIdAsync(BsonValue id, CancellationToken cancellationToken = default)
        {
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));

            return Task.FromResult(this.FindSync(BsonExpression.Create("_id = @0", id), cancellationToken: cancellationToken).FirstOrDefault());
        }

        /// <summary>
        /// Find the first document using predicate expression. Returns null if not found
        /// </summary>
        public Task<T> FindOneAsync(BsonExpression predicate, CancellationToken cancellationToken = default)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            return Task.FromResult(this.FindSync(predicate, cancellationToken: cancellationToken).FirstOrDefault());
        }

        /// <summary>
        /// Find the first document using predicate expression. Returns null if not found
        /// </summary>
        public Task<T> FindOneAsync(string predicate, BsonDocument parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(this.FindSync(BsonExpression.Create(predicate, parameters), cancellationToken: cancellationToken).FirstOrDefault());
        }

        /// <summary>
        /// Find the first document using predicate expression. Returns null if not found
        /// </summary>
        public Task<T> FindOneAsync(BsonExpression predicate, CancellationToken cancellationToken = default, params BsonValue[] args)
        {
            return Task.FromResult(this.FindSync(BsonExpression.Create(predicate, args), cancellationToken: cancellationToken).FirstOrDefault());
        }

        /// <summary>
        /// Find the first document using predicate expression. Returns null if not found
        /// </summary>
        public Task<T> FindOneAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(this.FindSync(predicate, cancellationToken: cancellationToken).FirstOrDefault());
        }

        /// <summary>
        /// Find the first document using defined query structure. Returns null if not found
        /// </summary>
        public Task<T> FindOneAsync(Query query, CancellationToken cancellationToken = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));

            return Task.FromResult(this.FindSync(query, cancellationToken: cancellationToken).FirstOrDefault());
        }

        /// <summary>
        /// Returns all documents inside collection order by _id index.
        /// </summary>
        public IAsyncEnumerable<T> FindAllAsync(CancellationToken cancellationToken = default)
        {
            return ToAsyncEnumerable(this.FindAllSync(), cancellationToken);
        }

        #endregion

        internal IEnumerable<T> FindSync(BsonExpression predicate, int skip = 0, int limit = int.MaxValue, CancellationToken cancellationToken = default)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));

            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();

            var liteQueryable = new LiteQueryable<T>(_engine, _mapper, _collection, new Query());

            if (_includes.Count > 0)
            {
                liteQueryable.Include(_includes);
            }

            liteQueryable.Where(predicate);

            liteQueryable.Skip(skip);
            liteQueryable.Limit(limit);

            return liteQueryable.Enumerate();
        }

        internal IEnumerable<T> FindSync(Query query, int skip = 0, int limit = int.MaxValue, CancellationToken cancellationToken = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));

            if (skip != 0) query.Offset = skip;
            if (limit != int.MaxValue) query.Limit = limit;

            var liteQueryable = new LiteQueryable<T>(_engine, _mapper, _collection, query);

            return liteQueryable.Enumerate();
        }

        internal IEnumerable<T> FindSync(Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue, CancellationToken cancellationToken = default)
        {
            return this.FindSync(_mapper.GetExpression(predicate), skip, limit, cancellationToken);
        }

        internal IEnumerable<T> FindAllSync()
        {
            var liteQueryable = new LiteQueryable<T>(_engine, _mapper, _collection, new Query());

            if (_includes.Count > 0)
            {
                liteQueryable.Include(_includes);
            }

            return liteQueryable.Enumerate();
        }

        private static async IAsyncEnumerable<TItem> ToAsyncEnumerable<TItem>(IEnumerable<TItem> source, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var item in source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }
}
