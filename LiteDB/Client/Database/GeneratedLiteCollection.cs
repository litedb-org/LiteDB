using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using LiteDB.Engine;

namespace LiteDB
{
    internal sealed class GeneratedLiteCollection<T> : ILiteCollection<T>
    {
        private readonly string _collection;
        private readonly ILiteEngine _engine;
        private readonly EntityMapper _entity;
        private readonly MemberMapper _id;
        private readonly BsonAutoId _autoId;
        private readonly GeneratedEntityMap<T> _map;
        private readonly BsonMapper _mapper;
        private readonly Func<GeneratedExecutionOptions> _getExecutionOptions;

        public string Name => _collection;

        public BsonAutoId AutoId => _autoId;

        public EntityMapper EntityMapper => _entity;

        internal GeneratedLiteCollection(
            string name,
            BsonAutoId autoId,
            ILiteEngine engine,
            EntityMapper entity,
            GeneratedEntityMap<T> map,
            BsonMapper mapper,
            Func<GeneratedExecutionOptions> getExecutionOptions)
        {
            _collection = name ?? throw new ArgumentNullException(nameof(name));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _entity = entity ?? throw new ArgumentNullException(nameof(entity));
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _getExecutionOptions = getExecutionOptions ?? throw new ArgumentNullException(nameof(getExecutionOptions));
            _id = entity.Id;
            _autoId = ResolveAutoId(_id, autoId);
        }

        public BsonValue Insert(T entity)
        {
            var options = _getExecutionOptions();
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            var document = _map.Serialize(entity, options);
            var removedId = RemoveEmptyId(document);

            _engine.Insert(_collection, new[] { document }, _autoId);

            var id = document["_id"];
            if (removedId)
            {
                _id.Setter(entity, id.RawValue);
            }

            return id;
        }

        public bool Update(T entity)
        {
            var options = _getExecutionOptions();
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            return _engine.Update(_collection, new[] { _map.Serialize(entity, options) }) > 0;
        }

        public T FindById(BsonValue id)
        {
            var options = _getExecutionOptions();
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));

            var query = new Query { Limit = 1 };
            query.Where.Add(BsonExpression.Create("_id = @0", id));

            using var reader = _engine.Query(_collection, query);
            return reader.Read() ? _map.Deserialize(reader.Current.AsDocument, options) : default;
        }

        public bool Delete(BsonValue id)
        {
            _getExecutionOptions();
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));

            return _engine.Delete(_collection, new[] { id }) > 0;
        }

        public int Count()
        {
            _getExecutionOptions();
            var count = 0;
            using var reader = _engine.Query(_collection, new Query());
            while (reader.Read()) count++;
            return count;
        }

        private static BsonAutoId ResolveAutoId(MemberMapper id, BsonAutoId requestedAutoId)
        {
            if (id is null || id.AutoId == false) return requestedAutoId;

            return id.DataType == typeof(int) || id.DataType == typeof(int?) ? BsonAutoId.Int32 :
                id.DataType == typeof(long) || id.DataType == typeof(long?) ? BsonAutoId.Int64 :
                id.DataType == typeof(Guid) || id.DataType == typeof(Guid?) ? BsonAutoId.Guid :
                BsonAutoId.ObjectId;
        }

        private bool RemoveEmptyId(BsonDocument document)
        {
            if (_id is null || document.TryGetValue("_id", out var id) == false) return false;

            var isEmpty =
                (_autoId == BsonAutoId.Int32 && id.IsInt32 && id.AsInt32 == 0) ||
                (_autoId == BsonAutoId.Int64 && id.IsInt64 && id.AsInt64 == 0) ||
                (_autoId == BsonAutoId.Guid && id.IsGuid && id.AsGuid == Guid.Empty) ||
                (_autoId == BsonAutoId.ObjectId && (id.IsNull || (id.IsObjectId && id.AsObjectId == ObjectId.Empty)));

            if (isEmpty == false) return false;

            document.Remove("_id");
            return true;
        }

        private IEnumerable<BsonDocument> SerializeForInsert(IEnumerable<T> entities, GeneratedExecutionOptions options)
        {
            foreach (var entity in entities)
            {
                var document = _map.Serialize(entity, options);
                var removedId = RemoveEmptyId(document);

                yield return document;

                if (removedId && _id is not null)
                {
                    _id.Setter(entity, document["_id"].RawValue);
                }
            }
        }

        private IEnumerable<BsonDocument> SerializeForUpdate(IEnumerable<T> entities, GeneratedExecutionOptions options)
        {
            foreach (var entity in entities)
            {
                yield return _map.Serialize(entity, options);
            }
        }

        private static NotSupportedException Unsupported(string operation) => new NotSupportedException(
            $"Generated collections support only Insert (single, explicit-ID, enumerable, and bulk), Update (single, explicit-ID, and enumerable), Upsert (single, explicit-ID, and enumerable), FindById, parameterless Count, Delete, and EnsureIndex. Operation '{operation}' is not supported.");

        public ILiteCollection<T> Include<K>(Expression<Func<T, K>> keySelector) => throw Unsupported(nameof(Include));
        public ILiteCollection<T> Include(BsonExpression keySelector) => throw Unsupported(nameof(Include));

        public bool Upsert(T entity)
        {
            var options = _getExecutionOptions();
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            var document = _map.Serialize(entity, options);
            var removedId = RemoveEmptyId(document);
            var count = _engine.Upsert(_collection, new[] { document }, _autoId);

            if (removedId && _id is not null)
            {
                _id.Setter(entity, document["_id"].RawValue);
            }

            return count == 1;
        }

        public int Upsert(IEnumerable<T> entities)
        {
            var options = _getExecutionOptions();
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            return _engine.Upsert(_collection, SerializeForInsert(entities, options), _autoId);
        }

        public bool Upsert(BsonValue id, T entity)
        {
            var options = _getExecutionOptions();
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));

            var document = _map.Serialize(entity, options);
            document["_id"] = id;

            return _engine.Upsert(_collection, new[] { document }, _autoId) > 0;
        }

        public bool Update(BsonValue id, T entity)
        {
            var options = _getExecutionOptions();
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));

            var document = _map.Serialize(entity, options);
            document["_id"] = id;

            return _engine.Update(_collection, new[] { document }) > 0;
        }

        public int Update(IEnumerable<T> entities)
        {
            var options = _getExecutionOptions();
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            return _engine.Update(_collection, SerializeForUpdate(entities, options));
        }

        public int UpdateMany(BsonExpression transform, BsonExpression predicate) => throw Unsupported(nameof(UpdateMany));
        public int UpdateMany(Expression<Func<T, T>> extend, Expression<Func<T, bool>> predicate) => throw Unsupported(nameof(UpdateMany));

        public void Insert(BsonValue id, T entity)
        {
            var options = _getExecutionOptions();
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));

            var document = _map.Serialize(entity, options);
            document["_id"] = id;

            _engine.Insert(_collection, new[] { document }, _autoId);
        }

        public int Insert(IEnumerable<T> entities)
        {
            var options = _getExecutionOptions();
            if (entities == null) throw new ArgumentNullException(nameof(entities));

            return _engine.Insert(_collection, SerializeForInsert(entities, options), _autoId);
        }

        public int InsertBulk(IEnumerable<T> entities, int batchSize = 5000)
        {
            var options = _getExecutionOptions();
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));

            var count = 0;
            // Do not use batchSize as the initial capacity: every positive value is
            // valid, including values too large to preallocate for a small source.
            var batch = new List<T>();

            foreach (var entity in entities)
            {
                batch.Add(entity);

                if (batch.Count == batchSize)
                {
                    count += _engine.Insert(_collection, SerializeForInsert(batch, options), _autoId);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                count += _engine.Insert(_collection, SerializeForInsert(batch, options), _autoId);
            }

            return count;
        }
        public bool EnsureIndex(string name, BsonExpression expression, bool unique = false)
        {
            _getExecutionOptions();
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            if (expression == null) throw new ArgumentNullException(nameof(expression));

            return _engine.EnsureIndex(_collection, name, expression, unique);
        }

        public bool EnsureIndex(BsonExpression expression, bool unique = false)
        {
            if (expression == null) throw new ArgumentNullException(nameof(expression));

            var name = Regex.Replace(expression.Source, @"[^a-z0-9]", "", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            return EnsureIndex(name, expression, unique);
        }

        public bool EnsureIndex<K>(Expression<Func<T, K>> keySelector, bool unique = false)
        {
            return EnsureIndex(GetIndexExpression(keySelector), unique);
        }

        public bool EnsureIndex<K>(string name, Expression<Func<T, K>> keySelector, bool unique = false)
        {
            return EnsureIndex(name, GetIndexExpression(keySelector), unique);
        }

        private BsonExpression GetIndexExpression<K>(Expression<Func<T, K>> keySelector)
        {
            if (keySelector == null) throw new ArgumentNullException(nameof(keySelector));

            var expression = _mapper.GetGeneratedIndexExpression(keySelector);
            if (typeof(K).IsEnumerable() && expression.IsScalar)
            {
                if (expression.Type != BsonExpressionType.Path)
                {
                    throw new LiteException(0, $"Expression `{expression.Source}` must return a enumerable expression");
                }

                expression = expression.Source + "[*]";
            }

            return expression;
        }
        public bool DropIndex(string name) => throw Unsupported(nameof(DropIndex));
        public ILiteQueryable<T> Query() => throw Unsupported(nameof(Query));
        public IEnumerable<T> Find(BsonExpression predicate, int skip = 0, int limit = int.MaxValue) => throw Unsupported(nameof(Find));
        public IEnumerable<T> Find(Query query, int skip = 0, int limit = int.MaxValue) => throw Unsupported(nameof(Find));
        public IEnumerable<T> Find(Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue) => throw Unsupported(nameof(Find));
        public T FindOne(BsonExpression predicate) => throw Unsupported(nameof(FindOne));
        public T FindOne(string predicate, BsonDocument parameters) => throw Unsupported(nameof(FindOne));
        public T FindOne(BsonExpression predicate, params BsonValue[] args) => throw Unsupported(nameof(FindOne));
        public T FindOne(Expression<Func<T, bool>> predicate) => throw Unsupported(nameof(FindOne));
        public T FindOne(Query query) => throw Unsupported(nameof(FindOne));
        public IEnumerable<T> FindAll() => throw Unsupported(nameof(FindAll));
        public int DeleteAll() => throw Unsupported(nameof(DeleteAll));
        public int DeleteMany(BsonExpression predicate) => throw Unsupported(nameof(DeleteMany));
        public int DeleteMany(string predicate, BsonDocument parameters) => throw Unsupported(nameof(DeleteMany));
        public int DeleteMany(string predicate, params BsonValue[] args) => throw Unsupported(nameof(DeleteMany));
        public int DeleteMany(Expression<Func<T, bool>> predicate) => throw Unsupported(nameof(DeleteMany));
        public int Count(BsonExpression predicate) => throw Unsupported(nameof(Count));
        public int Count(string predicate, BsonDocument parameters) => throw Unsupported(nameof(Count));
        public int Count(string predicate, params BsonValue[] args) => throw Unsupported(nameof(Count));
        public int Count(Expression<Func<T, bool>> predicate) => throw Unsupported(nameof(Count));
        public int Count(Query query) => throw Unsupported(nameof(Count));
        public long LongCount() => throw Unsupported(nameof(LongCount));
        public long LongCount(BsonExpression predicate) => throw Unsupported(nameof(LongCount));
        public long LongCount(string predicate, BsonDocument parameters) => throw Unsupported(nameof(LongCount));
        public long LongCount(string predicate, params BsonValue[] args) => throw Unsupported(nameof(LongCount));
        public long LongCount(Expression<Func<T, bool>> predicate) => throw Unsupported(nameof(LongCount));
        public long LongCount(Query query) => throw Unsupported(nameof(LongCount));
        public bool Exists(BsonExpression predicate) => throw Unsupported(nameof(Exists));
        public bool Exists(string predicate, BsonDocument parameters) => throw Unsupported(nameof(Exists));
        public bool Exists(string predicate, params BsonValue[] args) => throw Unsupported(nameof(Exists));
        public bool Exists(Expression<Func<T, bool>> predicate) => throw Unsupported(nameof(Exists));
        public bool Exists(Query query) => throw Unsupported(nameof(Exists));
        public BsonValue Min(BsonExpression keySelector) => throw Unsupported(nameof(Min));
        public BsonValue Min() => throw Unsupported(nameof(Min));
        public K Min<K>(Expression<Func<T, K>> keySelector) => throw Unsupported(nameof(Min));
        public BsonValue Max(BsonExpression keySelector) => throw Unsupported(nameof(Max));
        public BsonValue Max() => throw Unsupported(nameof(Max));
        public K Max<K>(Expression<Func<T, K>> keySelector) => throw Unsupported(nameof(Max));
    }
}
