using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
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
        /// Sort the documents of resultset in ascending (or descending) order according to a key (support only one OrderBy)
        /// </summary>
        public ILiteQueryable<T> OrderBy(BsonExpression keySelector, int order = Query.Ascending)
        {
            if (_query.OrderBy != null) throw new ArgumentException("ORDER BY already defined in this query builder");

            _query.OrderBy = keySelector;
            _query.Order = order;
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
        /// Sort the documents of resultset in descending order according to a key (support only one OrderBy)
        /// </summary>
        public ILiteQueryable<T> OrderByDescending(BsonExpression keySelector) => this.OrderBy(keySelector, Query.Descending);

        /// <summary>
        /// Sort the documents of resultset in descending order according to a key (support only one OrderBy)
        /// </summary>
        public ILiteQueryable<T> OrderByDescending<K>(Expression<Func<T, K>> keySelector) => this.OrderBy(keySelector, Query.Descending);

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

        private static void ValidateVectorArguments(float[] target, double maxDistance)
        {
            if (target == null || target.Length == 0) throw new ArgumentException("Target vector must be provided.", nameof(target));
            if (maxDistance < 0) throw new ArgumentOutOfRangeException(nameof(maxDistance), "Max distance must be non-negative.");
        }

        private static BsonExpression CreateVectorSimilarityFilter(BsonExpression fieldExpr, float[] target, double maxDistance)
        {
            if (fieldExpr == null) throw new ArgumentNullException(nameof(fieldExpr));

            ValidateVectorArguments(target, maxDistance);

            var targetArray = new BsonArray(target.Select(v => new BsonValue(v)));
            return BsonExpression.Create($"{fieldExpr.Source} VECTOR_SIM @0 <= @1", targetArray, new BsonValue(maxDistance));
        }

        public ILiteQueryable<T> WhereNear(string vectorField, float[] target, double maxDistance)
        {
            if (string.IsNullOrWhiteSpace(vectorField)) throw new ArgumentNullException(nameof(vectorField));

            var fieldExpr = BsonExpression.Create($"$.{vectorField}");
            return this.WhereNear(fieldExpr, target, maxDistance);
        }

        public ILiteQueryable<T> WhereNear(BsonExpression fieldExpr, float[] target, double maxDistance)
        {
            var filter = CreateVectorSimilarityFilter(fieldExpr, target, maxDistance);

            _query.Where.Add(filter);

            return this;
        }

        public ILiteQueryable<T> WhereNear<K>(Expression<Func<T, K>> field, float[] target, double maxDistance)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));

            var fieldExpr = _mapper.GetExpression(field);
            return this.WhereNear(fieldExpr, target, maxDistance);
        }
        
        public IEnumerable<T> FindNearest(string vectorField, float[] target, double maxDistance)
        {
            this.WhereNear(vectorField, target, maxDistance);
            return this.ToEnumerable();
        }


        public ILiteQueryableResult<T> TopKNear<K>(Expression<Func<T, K>> field, float[] target, int k)
        {
            var fieldExpr = _mapper.GetExpression(field);
            return this.TopKNear(fieldExpr, target, k);
        }

        public ILiteQueryableResult<T> TopKNear(string field, float[] target, int k)
        {
            var fieldExpr = BsonExpression.Create($"$.{field}");
            return this.TopKNear(fieldExpr, target, k);
        }

        public ILiteQueryableResult<T> TopKNear(BsonExpression fieldExpr, float[] target, int k)
        {
            if (fieldExpr == null) throw new ArgumentNullException(nameof(fieldExpr));
            if (target == null || target.Length == 0) throw new ArgumentException("Target vector must be provided.", nameof(target));
            if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k), "Top-K must be greater than zero.");

            var targetArray = new BsonArray(target.Select(v => new BsonValue(v)));

            // Build VECTOR_SIM as order clause
            var simExpr = BsonExpression.Create($"VECTOR_SIM({fieldExpr.Source}, @0)", targetArray);

            return this
                .OrderBy(simExpr, Query.Ascending)
                .Limit(k);
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
        public IBsonDataReader ExecuteReader()
        {
            _query.ExplainPlan = false;

            return _engine.Query(_collection, _query);
        }

        /// <summary>
        /// Execute query and return resultset as IEnumerable of documents
        /// </summary>
        public IEnumerable<BsonDocument> ToDocuments()
        {
            using (var reader = this.ExecuteReader())
            {
                while (reader.Read())
                {
                    yield return reader.Current as BsonDocument;
                }
            }
        }

        /// <summary>
        /// Execute query and return resultset as IEnumerable of T. If T is a ValueType or String, return values only (not documents)
        /// </summary>
        public IEnumerable<T> ToEnumerable()
        {
            if (_isSimpleType)
            {
                return this.ToDocuments()
                    .Select(x => x[x.Keys.First()])
                    .Select(x => (T)_mapper.Deserialize(typeof(T), x));
            }
            else
            {
                return this.ToDocuments()
                    .Select(x => (T)_mapper.Deserialize(typeof(T), x));
            }
        }

        /// <summary>
        /// Execute query and return results as a List
        /// </summary>
        public List<T> ToList()
        {
            return this.ToEnumerable().ToList();
        }

        /// <summary>
        /// Execute query and return results as an Array
        /// </summary>
        public T[] ToArray()
        {
            return this.ToEnumerable().ToArray();
        }

        /// <summary>
        /// Get execution plan over current query definition to see how engine will execute query
        /// </summary>
        public BsonDocument GetPlan()
        {
            _query.ExplainPlan = true;

            var reader = _engine.Query(_collection, _query);

            return reader.ToEnumerable().FirstOrDefault()?.AsDocument;
        }

        #endregion

        #region Execute Single/First

        /// <summary>
        /// Returns the only document of resultset, and throw an exception if there not exactly one document in the sequence
        /// </summary>
        public T Single()
        {
            return this.ToEnumerable().Single();
        }

        /// <summary>
        /// Returns the only document of resultset, or null if resultset are empty; this method throw an exception if there not exactly one document in the sequence
        /// </summary>
        public T SingleOrDefault()
        {
            return this.ToEnumerable().SingleOrDefault();
        }

        /// <summary>
        /// Returns first document of resultset
        /// </summary>
        public T First()
        {
            return this.ToEnumerable().First();
        }

        /// <summary>
        /// Returns first document of resultset or null if resultset are empty
        /// </summary>
        public T FirstOrDefault()
        {
            return this.ToEnumerable().FirstOrDefault();
        }

        #endregion

        #region Execute Count

        /// <summary>
        /// Execute Count methos in filter query
        /// </summary>
        public int Count()
        {
            var oldSelect = _query.Select;

            try
            {
                this.Select($"{{ count: COUNT(*._id) }}");
                var ret = this.ToDocuments().Single()["count"].AsInt32;

                return ret;
            }
            finally
            {
                _query.Select = oldSelect;
            }
        }

        /// <summary>
        /// Execute Count methos in filter query
        /// </summary>
        public long LongCount()
        {
            var oldSelect = _query.Select;

            try
            {
                this.Select($"{{ count: COUNT(*._id) }}");
                var ret = this.ToDocuments().Single()["count"].AsInt64;

                return ret;
            }
            finally
            {
                _query.Select = oldSelect;
            }
        }

        /// <summary>
        /// Returns true/false if query returns any result
        /// </summary>
        public bool Exists()
        {
            var oldSelect = _query.Select;

            try
            {
                this.Select($"{{ exists: ANY(*._id) }}");
                var ret = this.ToDocuments().Single()["exists"].AsBoolean;

                return ret;
            }
            finally
            {
                _query.Select = oldSelect;
            }
        }

        #endregion

        #region Execute Into

        public int Into(string newCollection, BsonAutoId autoId = BsonAutoId.ObjectId)
        {
            _query.Into = newCollection;
            _query.IntoAutoId = autoId;

            using (var reader = this.ExecuteReader())
            {
                return reader.Current.AsInt32;
            }
        }

        #endregion
    }
}