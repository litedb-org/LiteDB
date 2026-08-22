using System;
using System.Collections.Generic;
using System.Linq.Expressions;
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
        private readonly Action _validateConfiguration;

        public string Name => _collection;

        public BsonAutoId AutoId => _autoId;

        public EntityMapper EntityMapper => _entity;

        internal GeneratedLiteCollection(
            string name,
            BsonAutoId autoId,
            ILiteEngine engine,
            EntityMapper entity,
            GeneratedEntityMap<T> map,
            Action validateConfiguration)
        {
            _collection = name ?? throw new ArgumentNullException(nameof(name));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _entity = entity ?? throw new ArgumentNullException(nameof(entity));
            _map = map ?? throw new ArgumentNullException(nameof(map));
            _validateConfiguration = validateConfiguration ?? throw new ArgumentNullException(nameof(validateConfiguration));
            _id = entity.Id;
            _autoId = ResolveAutoId(_id, autoId);
        }

        public BsonValue Insert(T entity)
        {
            _validateConfiguration();
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            var document = _map.Serialize(entity);
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
            _validateConfiguration();
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            return _engine.Update(_collection, new[] { _map.Serialize(entity) }) > 0;
        }

        public T FindById(BsonValue id)
        {
            _validateConfiguration();
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));

            var query = new Query { Limit = 1 };
            query.Where.Add(BsonExpression.Create("_id = @0", id));

            using var reader = _engine.Query(_collection, query);
            return reader.Read() ? _map.Deserialize(reader.Current.AsDocument) : default;
        }

        public bool Delete(BsonValue id)
        {
            _validateConfiguration();
            if (id == null || id.IsNull) throw new ArgumentNullException(nameof(id));

            return _engine.Delete(_collection, new[] { id }) > 0;
        }

        public int Count()
        {
            _validateConfiguration();
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

        private static NotSupportedException Unsupported(string operation) => new NotSupportedException(
            $"Generated execution maps currently support Insert, Update, FindById, Count, and Delete only. '{operation}' is not available during the generated execution proof of concept.");

        public ILiteCollection<T> Include<K>(Expression<Func<T, K>> keySelector) => throw Unsupported(nameof(Include));
        public ILiteCollection<T> Include(BsonExpression keySelector) => throw Unsupported(nameof(Include));
        public bool Upsert(T entity) => throw Unsupported(nameof(Upsert));
        public int Upsert(IEnumerable<T> entities) => throw Unsupported(nameof(Upsert));
        public bool Upsert(BsonValue id, T entity) => throw Unsupported(nameof(Upsert));
        public bool Update(BsonValue id, T entity) => throw Unsupported(nameof(Update));
        public int Update(IEnumerable<T> entities) => throw Unsupported(nameof(Update));
        public int UpdateMany(BsonExpression transform, BsonExpression predicate) => throw Unsupported(nameof(UpdateMany));
        public int UpdateMany(Expression<Func<T, T>> extend, Expression<Func<T, bool>> predicate) => throw Unsupported(nameof(UpdateMany));
        public void Insert(BsonValue id, T entity) => throw Unsupported(nameof(Insert));
        public int Insert(IEnumerable<T> entities) => throw Unsupported(nameof(Insert));
        public int InsertBulk(IEnumerable<T> entities, int batchSize = 5000) => throw Unsupported(nameof(InsertBulk));
        public bool EnsureIndex(string name, BsonExpression expression, bool unique = false) => throw Unsupported(nameof(EnsureIndex));
        public bool EnsureIndex(BsonExpression expression, bool unique = false) => throw Unsupported(nameof(EnsureIndex));
        public bool EnsureIndex<K>(Expression<Func<T, K>> keySelector, bool unique = false) => throw Unsupported(nameof(EnsureIndex));
        public bool EnsureIndex<K>(string name, Expression<Func<T, K>> keySelector, bool unique = false) => throw Unsupported(nameof(EnsureIndex));
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
