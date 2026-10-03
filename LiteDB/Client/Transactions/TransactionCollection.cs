using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    /// <summary>Guards client mapping as well as engine execution for bound collections.</summary>
    internal sealed class TransactionCollection<T> : ILiteCollection<T>
    {
        private readonly LiteTransaction Owner;
        private readonly ILiteCollection<T> Inner;
        internal TransactionCollection(LiteTransaction owner, ILiteCollection<T> inner) { Owner = owner; Inner = inner; }
        internal TResult Vector<TResult>(Func<LiteCollection<T>, TResult> action) => Owner.Run(() => action((LiteCollection<T>)Inner));
        public string Name => Owner.Run(() => Inner.Name);
        public BsonAutoId AutoId => Owner.Run(() => Inner.AutoId);
        public EntityMapper EntityMapper => Owner.Run(() => Inner.EntityMapper);
        public ILiteCollection<T> Include<K>(Expression<Func<T, K>> keySelector) => Owner.Run(() => (ILiteCollection<T>)new TransactionCollection<T>(Owner, Inner.Include<K>(keySelector)));
        public ILiteCollection<T> Include(BsonExpression keySelector) => Owner.Run(() => (ILiteCollection<T>)new TransactionCollection<T>(Owner, Inner.Include(keySelector)));
        public bool Upsert(T entity) => Owner.Run(() => Inner.Upsert(entity));
        public int Upsert(IEnumerable<T> entities) => Owner.Run(() => Inner.Upsert(entities));
        public bool Upsert(BsonValue id, T entity) => Owner.Run(() => Inner.Upsert(id, entity));
        public bool Update(T entity) => Owner.Run(() => Inner.Update(entity));
        public bool Update(BsonValue id, T entity) => Owner.Run(() => Inner.Update(id, entity));
        public int Update(IEnumerable<T> entities) => Owner.Run(() => Inner.Update(entities));
        public int UpdateMany(BsonExpression transform, BsonExpression predicate) => Owner.Run(() => Inner.UpdateMany(transform, predicate));
        public int UpdateMany(Expression<Func<T, T>> extend, Expression<Func<T, bool>> predicate) => Owner.Run(() => Inner.UpdateMany(extend, predicate));
        public BsonValue Insert(T entity) => Owner.Run(() => Inner.Insert(entity));
        public void Insert(BsonValue id, T entity) => Owner.Run(() => { Inner.Insert(id, entity); return true; });
        public int Insert(IEnumerable<T> entities) => Owner.Run(() => Inner.Insert(entities));
        public int InsertBulk(IEnumerable<T> entities, int batchSize = 5000) => Owner.Run(() => Inner.InsertBulk(entities, batchSize));
        public bool EnsureIndex(string name, BsonExpression expression, bool unique = false) => Owner.Run(() => Inner.EnsureIndex(name, expression, unique));
        public bool EnsureIndex(BsonExpression expression, bool unique = false) => Owner.Run(() => Inner.EnsureIndex(expression, unique));
        public bool EnsureIndex<K>(Expression<Func<T, K>> keySelector, bool unique = false) => Owner.Run(() => Inner.EnsureIndex<K>(keySelector, unique));
        public bool EnsureIndex<K>(string name, Expression<Func<T, K>> keySelector, bool unique = false) => Owner.Run(() => Inner.EnsureIndex<K>(name, keySelector, unique));
        public bool DropIndex(string name) => Owner.Run(() => Inner.DropIndex(name));
        public ILiteQueryable<T> Query() => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Inner.Query()));
        public IEnumerable<T> Find(BsonExpression predicate, int skip = 0, int limit = int.MaxValue) => Owner.Run(() => (IEnumerable<T>)new TransactionEnumerable<T>(Owner, Inner.Find(predicate, skip, limit)));
        public IEnumerable<T> Find(Query query, int skip = 0, int limit = int.MaxValue) => Owner.Run(() => (IEnumerable<T>)new TransactionEnumerable<T>(Owner, Inner.Find(query, skip, limit)));
        public IEnumerable<T> Find(Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue) => Owner.Run(() => (IEnumerable<T>)new TransactionEnumerable<T>(Owner, Inner.Find(predicate, skip, limit)));
        public T FindById(BsonValue id) => Owner.Run(() => Inner.FindById(id));
        public T FindOne(BsonExpression predicate) => Owner.Run(() => Inner.FindOne(predicate));
        public T FindOne(string predicate, BsonDocument parameters) => Owner.Run(() => Inner.FindOne(predicate, parameters));
        public T FindOne(BsonExpression predicate, params BsonValue[] args) => Owner.Run(() => Inner.FindOne(predicate, args));
        public T FindOne(Expression<Func<T, bool>> predicate) => Owner.Run(() => Inner.FindOne(predicate));
        public T FindOne(Query query) => Owner.Run(() => Inner.FindOne(query));
        public IEnumerable<T> FindAll() => Owner.Run(() => (IEnumerable<T>)new TransactionEnumerable<T>(Owner, Inner.FindAll()));
        public bool Delete(BsonValue id) => Owner.Run(() => Inner.Delete(id));
        public int DeleteAll() => Owner.Run(() => Inner.DeleteAll());
        public int DeleteMany(BsonExpression predicate) => Owner.Run(() => Inner.DeleteMany(predicate));
        public int DeleteMany(string predicate, BsonDocument parameters) => Owner.Run(() => Inner.DeleteMany(predicate, parameters));
        public int DeleteMany(string predicate, params BsonValue[] args) => Owner.Run(() => Inner.DeleteMany(predicate, args));
        public int DeleteMany(Expression<Func<T, bool>> predicate) => Owner.Run(() => Inner.DeleteMany(predicate));
        public int Count() => Owner.Run(() => Inner.Count());
        public int Count(BsonExpression predicate) => Owner.Run(() => Inner.Count(predicate));
        public int Count(string predicate, BsonDocument parameters) => Owner.Run(() => Inner.Count(predicate, parameters));
        public int Count(string predicate, params BsonValue[] args) => Owner.Run(() => Inner.Count(predicate, args));
        public int Count(Expression<Func<T, bool>> predicate) => Owner.Run(() => Inner.Count(predicate));
        public int Count(Query query) => Owner.Run(() => Inner.Count(query));
        public long LongCount() => Owner.Run(() => Inner.LongCount());
        public long LongCount(BsonExpression predicate) => Owner.Run(() => Inner.LongCount(predicate));
        public long LongCount(string predicate, BsonDocument parameters) => Owner.Run(() => Inner.LongCount(predicate, parameters));
        public long LongCount(string predicate, params BsonValue[] args) => Owner.Run(() => Inner.LongCount(predicate, args));
        public long LongCount(Expression<Func<T, bool>> predicate) => Owner.Run(() => Inner.LongCount(predicate));
        public long LongCount(Query query) => Owner.Run(() => Inner.LongCount(query));
        public bool Exists(BsonExpression predicate) => Owner.Run(() => Inner.Exists(predicate));
        public bool Exists(string predicate, BsonDocument parameters) => Owner.Run(() => Inner.Exists(predicate, parameters));
        public bool Exists(string predicate, params BsonValue[] args) => Owner.Run(() => Inner.Exists(predicate, args));
        public bool Exists(Expression<Func<T, bool>> predicate) => Owner.Run(() => Inner.Exists(predicate));
        public bool Exists(Query query) => Owner.Run(() => Inner.Exists(query));
        public BsonValue Min(BsonExpression keySelector) => Owner.Run(() => Inner.Min(keySelector));
        public BsonValue Min() => Owner.Run(() => Inner.Min());
        public K Min<K>(Expression<Func<T, K>> keySelector) => Owner.Run(() => Inner.Min<K>(keySelector));
        public BsonValue Max(BsonExpression keySelector) => Owner.Run(() => Inner.Max(keySelector));
        public BsonValue Max() => Owner.Run(() => Inner.Max());
        public K Max<K>(Expression<Func<T, K>> keySelector) => Owner.Run(() => Inner.Max<K>(keySelector));
    }
}
