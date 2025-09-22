using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// An IQueryable-like class to write fluent query in documents in collection.
    /// </summary>
    public class LiteQueryable<T> : ILiteQueryable<T>
    {
        protected readonly ILiteEngine _engine;
        protected readonly BsonMapper _mapper;
        protected readonly string _collection;
        protected readonly Query _query;

        // indicate that T type are simple and result are inside first document fields (query always return a BsonDocument)
        private readonly bool _isSimpleType = Reflection.IsSimpleType(typeof(T));

        internal LiteQueryable(ILiteEngine engine, BsonMapper mapper, string collection, Query query)
        {
            _engine = engine;
            _mapper = mapper;
            _collection = collection;
            _query = query;
        }

        #region Includes

        /// <summary>
        /// Load cross reference documents from path expression (DbRef reference)
        /// </summary>
        public ILiteQueryable<T> Include<K>(Expression<Func<T, K>> path)
        {
            _query.Includes.Add(_mapper.GetExpression(path));
            return this;
        }

        /// <summary>
        /// Load cross reference documents from path expression (DbRef reference)
        /// </summary>
        public ILiteQueryable<T> Include(BsonExpression path)
        {
            _query.Includes.Add(path);
            return this;
        }

        /// <summary>
        /// Load cross reference documents from path expression (DbRef reference)
        /// </summary>
        public ILiteQueryable<T> Include(List<BsonExpression> paths)
        {
            _query.Includes.AddRange(paths);
            return this;
        }

        #endregion

        #region Where

        /// <summary>
        /// Filters a sequence of documents based on a predicate expression
        /// </summary>
        public ILiteQueryable<T> Where(BsonExpression predicate)
        {
            _query.Where.Add(predicate);
            return this;
        }

        /// <summary>
        /// Filters a sequence of documents based on a predicate expression
        /// </summary>
        public ILiteQueryable<T> Where(string predicate, BsonDocument parameters)
        {
            _query.Where.Add(BsonExpression.Create(predicate, parameters));
            return this;
        }

        /// <summary>
        /// Filters a sequence of documents based on a predicate expression
        /// </summary>
        public ILiteQueryable<T> Where(string predicate, params BsonValue[] args)
        {
            _query.Where.Add(BsonExpression.Create(predicate, args));
            return this;
        }

        /// <summary>
        /// Filters a sequence of documents based on a predicate expression
        /// </summary>
        public ILiteQueryable<T> Where(Expression<Func<T, bool>> predicate)
        {
            return this.Where(_mapper.GetExpression(predicate));
        }

        #endregion

        #region OrderBy

        /// <summary>
        /// Sort the documents of resultset in ascending (or descending) order according to a key.
        /// </summary>
        public ILiteQueryable<T> OrderBy(BsonExpression keySelector, int order = Query.Ascending)
        {
            if (_query.OrderBy.Count > 0) throw new ArgumentException("Multiple OrderBy calls are not supported. Use ThenBy for additional sort keys.");

            _query.OrderBy.Add(new QueryOrder(keySelector, order));
            return this;
        }

        /// <summary>
        /// Sort the documents of resultset in ascending (or descending) order according to a key (support only one OrderBy)
        /// </summary>
        public ILiteQueryable<T> OrderBy<K>(Expression<Func<T, K>> keySelector, int order = Query.Ascending)
        {
            return this.OrderBy(_mapper.GetExpression(keySelector), order);
        }

        /// <summary>
        /// Sort the documents of resultset in descending order according to a key.
        /// </summary>
        public ILiteQueryable<T> OrderByDescending(BsonExpression keySelector) => this.OrderBy(keySelector, Query.Descending);

        /// <summary>
        /// Sort the documents of resultset in descending order according to a key.
        /// </summary>
        public ILiteQueryable<T> OrderByDescending<K>(Expression<Func<T, K>> keySelector) => this.OrderBy(keySelector, Query.Descending);

        /// <summary>
        /// Appends an ascending sort expression that is applied when previous keys are equal.
        /// </summary>
        public ILiteQueryable<T> ThenBy(BsonExpression keySelector)
        {
            if (_query.OrderBy.Count == 0) return this.OrderBy(keySelector, Query.Ascending);

            _query.OrderBy.Add(new QueryOrder(keySelector, Query.Ascending));
            return this;
        }

        /// <summary>
        /// Appends an ascending sort expression that is applied when previous keys are equal.
        /// </summary>
        public ILiteQueryable<T> ThenBy<K>(Expression<Func<T, K>> keySelector)
        {
            return this.ThenBy(_mapper.GetExpression(keySelector));
        }

        /// <summary>
        /// Appends a descending sort expression that is applied when previous keys are equal.
        /// </summary>
        public ILiteQueryable<T> ThenByDescending(BsonExpression keySelector)
        {
            if (_query.OrderBy.Count == 0) return this.OrderBy(keySelector, Query.Descending);

            _query.OrderBy.Add(new QueryOrder(keySelector, Query.Descending));
            return this;
        }

        /// <summary>
        /// Appends a descending sort expression that is applied when previous keys are equal.
        /// </summary>
        public ILiteQueryable<T> ThenByDescending<K>(Expression<Func<T, K>> keySelector)
        {
            return this.ThenByDescending(_mapper.GetExpression(keySelector));
        }

        #endregion

        #region GroupBy

        /// <summary>
        /// Groups the documents of resultset according to a specified key selector expression (support only one GroupBy)
        /// </summary>
        public ILiteQueryable<T> GroupBy(BsonExpression keySelector)
        {
            if (_query.GroupBy != null) throw new ArgumentException("GROUP BY already defined in this query");

            _query.GroupBy = keySelector;
            return this;
        }

        #endregion

        #region Having

        /// <summary>
        /// Filter documents after group by pipe according to predicate expression (requires GroupBy and support only one Having)
        /// </summary>
        public ILiteQueryable<T> Having(BsonExpression predicate)
        {
            if (_query.Having != null) throw new ArgumentException("HAVING already defined in this query");

            _query.Having = predicate;
            return this;
        }

        #endregion

        #region Select

        /// <summary>
        /// Transform input document into a new output document. Can be used with each document, group by or all source
        /// </summary>
        public ILiteQueryableResult<BsonDocument> Select(BsonExpression selector)
        {
            _query.Select = selector;

            return new LiteQueryable<BsonDocument>(_engine, _mapper, _collection, _query);
        }

        /// <summary>
        /// Project each document of resultset into a new document/value based on selector expression
        /// </summary>
        public ILiteQueryableResult<K> Select<K>(Expression<Func<T, K>> selector)
        {
            if (_query.GroupBy != null) throw new ArgumentException("Use Select(BsonExpression selector) when using GroupBy query");

            _query.Select = _mapper.GetExpression(selector);

            return new LiteQueryable<K>(_engine, _mapper, _collection, _query);
        }

        #endregion

        #region Offset/Limit/ForUpdate

        /// <summary>
        /// Execute query locking collection in write mode. This is avoid any other thread change results after read document and before transaction ends
        /// </summary>
        public ILiteQueryableResult<T> ForUpdate()
        {
            _query.ForUpdate = true;
            return this;
        }

        /// <summary>
        /// Bypasses a specified number of documents in resultset and retun the remaining documents (same as Skip)
        /// </summary>
        public ILiteQueryableResult<T> Offset(int offset)
        {
            _query.Offset = offset;
            return this;
        }

        /// <summary>
        /// Bypasses a specified number of documents in resultset and retun the remaining documents (same as Offset)
        /// </summary>
        public ILiteQueryableResult<T> Skip(int offset) => this.Offset(offset);

        /// <summary>
        /// Return a specified number of contiguous documents from start of resultset
        /// </summary>
        public ILiteQueryableResult<T> Limit(int limit)
        {
            _query.Limit = limit;
            return this;
        }

        #endregion

        #region Execute Result

        /// <summary>
        /// Execute query and returns resultset as generic BsonDataReader
        /// </summary>
        public Task<IBsonDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(this.ExecuteReaderCore());
        }

        /// <summary>
        /// Execute query and return resultset as asynchronous sequence of documents
        /// </summary>
        public async IAsyncEnumerable<BsonDocument> ToDocumentsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var document in this.EnumerateDocumentsAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return document;
            }
        }

        /// <summary>
        /// Execute query and return resultset as asynchronous sequence of T. If T is a ValueType or String, return values only (not documents)
        /// </summary>
        public async IAsyncEnumerable<T> ToAsyncEnumerable([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var item in this.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }

        /// <summary>
        /// Execute query and return results as a List
        /// </summary>
        public async Task<List<T>> ToListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var list = new List<T>();

            await foreach (var item in this.ToAsyncEnumerable(cancellationToken).ConfigureAwait(false))
            {
                list.Add(item);
            }

            return list;
        }

        /// <summary>
        /// Execute query and return results as an Array
        /// </summary>
        public async Task<T[]> ToArrayAsync(CancellationToken cancellationToken = default)
        {
            var list = await this.ToListAsync(cancellationToken).ConfigureAwait(false);
            return list.ToArray();
        }

        /// <summary>
        /// Get execution plan over current query definition to see how engine will execute query
        /// </summary>
        public async Task<BsonDocument> GetPlanAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var previousExplain = _query.ExplainPlan;
            _query.ExplainPlan = true;

            try
            {
                await using var reader = _engine.Query(_collection, _query);

                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return reader.Current.AsDocument;
                }

                return null;
            }
            finally
            {
                _query.ExplainPlan = previousExplain;
            }
        }

        #endregion

        #region Execute Single/First

        /// <summary>
        /// Returns the only document of resultset, and throw an exception if there not exactly one document in the sequence
        /// </summary>
        public async Task<T> SingleAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            T value = default;
            var found = false;

            await foreach (var item in this.ToAsyncEnumerable(cancellationToken).ConfigureAwait(false))
            {
                if (found)
                {
                    throw new InvalidOperationException("Sequence contains more than one element");
                }

                found = true;
                value = item;
            }

            if (!found)
            {
                throw new InvalidOperationException("Sequence contains no elements");
            }

            return value;
        }

        /// <summary>
        /// Returns the only document of resultset, or null if resultset are empty; this method throw an exception if there not exactly one document in the sequence
        /// </summary>
        public async Task<T> SingleOrDefaultAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            T value = default;
            var found = false;

            await foreach (var item in this.ToAsyncEnumerable(cancellationToken).ConfigureAwait(false))
            {
                if (found)
                {
                    throw new InvalidOperationException("Sequence contains more than one element");
                }

                found = true;
                value = item;
            }

            return value;
        }

        /// <summary>
        /// Returns first document of resultset
        /// </summary>
        public async Task<T> FirstAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await foreach (var item in this.ToAsyncEnumerable(cancellationToken).ConfigureAwait(false))
            {
                return item;
            }

            throw new InvalidOperationException("Sequence contains no elements");
        }

        /// <summary>
        /// Returns first document of resultset or null if resultset are empty
        /// </summary>
        public async Task<T> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await foreach (var item in this.ToAsyncEnumerable(cancellationToken).ConfigureAwait(false))
            {
                return item;
            }

            return default;
        }

        #endregion

        #region Execute Count

        /// <summary>
        /// Execute Count method in filter query
        /// </summary>
        public async Task<int> CountAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var oldSelect = _query.Select;

            try
            {
                this.Select($"{{ count: COUNT(*._id) }}");

                await foreach (var doc in this.ToDocumentsAsync(cancellationToken).ConfigureAwait(false))
                {
                    return doc["count"].AsInt32;
                }

                return 0;
            }
            finally
            {
                _query.Select = oldSelect;
            }
        }

        /// <summary>
        /// Execute Count method in filter query
        /// </summary>
        public async Task<long> LongCountAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var oldSelect = _query.Select;

            try
            {
                this.Select($"{{ count: COUNT(*._id) }}");

                await foreach (var doc in this.ToDocumentsAsync(cancellationToken).ConfigureAwait(false))
                {
                    return doc["count"].AsInt64;
                }

                return 0L;
            }
            finally
            {
                _query.Select = oldSelect;
            }
        }

        /// <summary>
        /// Returns true/false if query returns any result
        /// </summary>
        public async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var oldSelect = _query.Select;

            try
            {
                this.Select($"{{ exists: ANY(*._id) }}");

                await foreach (var doc in this.ToDocumentsAsync(cancellationToken).ConfigureAwait(false))
                {
                    return doc["exists"].AsBoolean;
                }

                return false;
            }
            finally
            {
                _query.Select = oldSelect;
            }
        }

        #endregion

        #region Execute Into

        public async Task<int> IntoAsync(string newCollection, BsonAutoId autoId = BsonAutoId.ObjectId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _query.Into = newCollection;
            _query.IntoAutoId = autoId;

            await using var reader = this.ExecuteReaderCore();

            return reader.Current.AsInt32;
        }

        #endregion

        private IBsonDataReader ExecuteReaderCore()
        {
            _query.ExplainPlan = false;

            return _engine.Query(_collection, _query);
        }

        internal async IAsyncEnumerable<BsonDocument> EnumerateDocumentsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await using var reader = await this.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.Current is BsonDocument document)
                {
                    yield return document;
                }
                else
                {
                    yield return reader.Current.AsDocument;
                }
            }
        }

        internal async IAsyncEnumerable<T> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_isSimpleType)
            {
                await foreach (var doc in this.EnumerateDocumentsAsync(cancellationToken).ConfigureAwait(false))
                {
                    var value = doc[doc.Keys.First()];
                    yield return (T)_mapper.Deserialize(typeof(T), value);
                }
            }
            else
            {
                await foreach (var doc in this.EnumerateDocumentsAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield return (T)_mapper.Deserialize(typeof(T), doc);
                }
            }
        }

        internal IEnumerable<BsonDocument> EnumerateDocuments()
        {
            using (var reader = this.ExecuteReaderCore())
            {
                while (reader.Read())
                {
                    yield return reader.Current as BsonDocument;
                }
            }
        }

        internal IEnumerable<T> Enumerate()
        {
            if (_isSimpleType)
            {
                return this.EnumerateDocuments()
                    .Select(x => x[x.Keys.First()])
                    .Select(x => (T)_mapper.Deserialize(typeof(T), x));
            }
            else
            {
                return this.EnumerateDocuments()
                    .Select(x => (T)_mapper.Deserialize(typeof(T), x));
            }
        }
    }
}