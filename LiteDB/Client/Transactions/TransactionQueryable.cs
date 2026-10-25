using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    internal sealed class TransactionQueryable<T> : TransactionQueryResult<T>, ILiteQueryable<T>
    {
        private readonly ILiteQueryable<T> Query;
        internal TransactionQueryable(LiteTransaction owner, ILiteQueryable<T> query) : base(owner, query) { Query = query; }
        public T SingleOrDefaultById(BsonValue id) => Owner.Run(() => Query.SingleOrDefaultById(id));
        public ILiteQueryable<T> Include(BsonExpression path) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.Include(path)));
        public ILiteQueryable<T> Include(List<BsonExpression> paths) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.Include(paths)));
        public ILiteQueryable<T> Include<K>(Expression<Func<T, K>> path) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.Include<K>(path)));
        public ILiteQueryable<T> Where(BsonExpression predicate) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.Where(predicate)));
        public ILiteQueryable<T> Where(string predicate, BsonDocument parameters) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.Where(predicate, parameters)));
        public ILiteQueryable<T> Where(string predicate, params BsonValue[] args) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.Where(predicate, args)));
        public ILiteQueryable<T> Where(Expression<Func<T, bool>> predicate) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.Where(predicate)));
        public ILiteQueryable<T> OrderBy(BsonExpression keySelector, int order = 1) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.OrderBy(keySelector, order)));
        public ILiteQueryable<T> OrderBy<K>(Expression<Func<T, K>> keySelector, int order = 1) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.OrderBy<K>(keySelector, order)));
        public ILiteQueryable<T> OrderByDescending(BsonExpression keySelector) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.OrderByDescending(keySelector)));
        public ILiteQueryable<T> OrderByDescending<K>(Expression<Func<T, K>> keySelector) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.OrderByDescending<K>(keySelector)));
        public ILiteQueryable<T> ThenBy(BsonExpression keySelector) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.ThenBy(keySelector)));
        public ILiteQueryable<T> ThenBy<K>(Expression<Func<T, K>> keySelector) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.ThenBy<K>(keySelector)));
        public ILiteQueryable<T> ThenByDescending(BsonExpression keySelector) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.ThenByDescending(keySelector)));
        public ILiteQueryable<T> ThenByDescending<K>(Expression<Func<T, K>> keySelector) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.ThenByDescending<K>(keySelector)));
        public ILiteQueryable<IGrouping<K, T>> GroupBy<K>(Expression<Func<T, K>> keySelector) => Owner.Run(() => (ILiteQueryable<IGrouping<K, T>>)new TransactionQueryable<IGrouping<K, T>>(Owner, Query.GroupBy<K>(keySelector)));
        public ILiteQueryable<T> GroupBy(BsonExpression keySelector) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.GroupBy(keySelector)));
        public ILiteQueryable<T> Having(BsonExpression predicate) => Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, Query.Having(predicate)));
        public ILiteQueryable<BsonDocument> Select(BsonExpression selector) => Owner.Run(() => (ILiteQueryable<BsonDocument>)new TransactionQueryable<BsonDocument>(Owner, Query.Select(selector)));
        public ILiteQueryable<K> Select<K>(Expression<Func<T, K>> selector) => Owner.Run(() => (ILiteQueryable<K>)new TransactionQueryable<K>(Owner, Query.Select<K>(selector)));
    }
}
