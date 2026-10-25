using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    internal class TransactionQueryResult<T> : ILiteQueryableResult<T>
    {
        protected readonly LiteTransaction Owner;
        protected readonly ILiteQueryableResult<T> Inner;
        internal TransactionQueryResult(LiteTransaction owner, ILiteQueryableResult<T> inner) { Owner = owner; Inner = inner; }
        internal ILiteQueryable<T> VectorQuery(Func<LiteQueryable<T>, ILiteQueryable<T>> action) =>
            Owner.Run(() => (ILiteQueryable<T>)new TransactionQueryable<T>(Owner, action((LiteQueryable<T>)Inner)));
        internal ILiteQueryableResult<T> VectorResult(Func<LiteQueryable<T>, ILiteQueryableResult<T>> action) =>
            Owner.Run(() => (ILiteQueryableResult<T>)new TransactionQueryResult<T>(Owner, action((LiteQueryable<T>)Inner)));
        internal IEnumerable<TResult> VectorEnumerable<TResult>(Func<LiteQueryable<T>, IEnumerable<TResult>> action) =>
            Owner.Run(() => (IEnumerable<TResult>)new TransactionEnumerable<TResult>(Owner, action((LiteQueryable<T>)Inner)));
        public ILiteQueryableResult<T> Limit(int limit) => Owner.Run(() => (ILiteQueryableResult<T>)new TransactionQueryResult<T>(Owner, Inner.Limit(limit)));
        public ILiteQueryableResult<T> Skip(int offset) => Owner.Run(() => (ILiteQueryableResult<T>)new TransactionQueryResult<T>(Owner, Inner.Skip(offset)));
        public ILiteQueryableResult<T> Offset(int offset) => Owner.Run(() => (ILiteQueryableResult<T>)new TransactionQueryResult<T>(Owner, Inner.Offset(offset)));
        public ILiteQueryableResult<T> ForUpdate() => Owner.Run(() => (ILiteQueryableResult<T>)new TransactionQueryResult<T>(Owner, Inner.ForUpdate()));
        public BsonDocument GetPlan() => Owner.Run(() => Inner.GetPlan());
        public IBsonDataReader ExecuteReader() => Owner.Run(() => (IBsonDataReader)new GuardedTransactionReader(Owner, Inner.ExecuteReader()));
        public IEnumerable<BsonDocument> ToDocuments() => Owner.Run(() => (IEnumerable<BsonDocument>)new TransactionEnumerable<BsonDocument>(Owner, Inner.ToDocuments()));
        public IEnumerable<T> ToEnumerable() => Owner.Run(() => (IEnumerable<T>)new TransactionEnumerable<T>(Owner, Inner.ToEnumerable()));
        public List<T> ToList() => Owner.Run(() => Inner.ToList());
        public T[] ToArray() => Owner.Run(() => Inner.ToArray());
        public int Into(string newCollection, BsonAutoId autoId = BsonAutoId.ObjectId) => Owner.Run(() => Inner.Into(newCollection, autoId));
        public T First() => Owner.Run(() => Inner.First());
        public T FirstOrDefault() => Owner.Run(() => Inner.FirstOrDefault());
        public T Single() => Owner.Run(() => Inner.Single());
        public T SingleOrDefault() => Owner.Run(() => Inner.SingleOrDefault());
        public int Count() => Owner.Run(() => Inner.Count());
        public long LongCount() => Owner.Run(() => Inner.LongCount());
        public bool Exists() => Owner.Run(() => Inner.Exists());
    }
}
