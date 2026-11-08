using System;
using System.Collections.Generic;
using LiteDB.Engine;
using LiteDB.Vector;

namespace LiteDB
{
    /// <summary>
    /// The engine seen by ordinary database objects. A mapper or input callback running inside a
    /// transaction handle's operation must not inherit that handle: each call clears the binding.
    /// </summary>
    internal sealed class UnboundEngine : ILiteEngine
    {
        private readonly ILiteEngine _inner;
        private readonly TransactionHandles _handles;
        internal UnboundEngine(ILiteEngine inner, TransactionHandles handles = null) { _inner = inner; _handles = handles; }
        private T Call<T>(Func<T> action)
        {
            _handles?.ThrowIfClosed();
            return TransactionContext.Unbound(action);
        }
        public int Checkpoint()
        { return Call(() => _inner.Checkpoint()); }
        public long Rebuild(RebuildOptions options)
        { return Call(() => _inner.Rebuild(options)); }
        public bool BeginTrans()
        { return Call(() => _inner.BeginTrans()); }
        public bool Commit()
        { return Call(() => _inner.Commit()); }
        public bool Rollback()
        { return Call(() => _inner.Rollback()); }
        public IBsonDataReader Query(string collection, Query query)
        { return new Reader(Call(() => _inner.Query(collection, query))); }
        public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        { return Call(() => _inner.Insert(collection, docs, autoId)); }
        public int Update(string collection, IEnumerable<BsonDocument> docs)
        { return Call(() => _inner.Update(collection, docs)); }
        public int UpdateMany(string collection, BsonExpression transform, BsonExpression predicate)
        { return Call(() => _inner.UpdateMany(collection, transform, predicate)); }
        public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        { return Call(() => _inner.Upsert(collection, docs, autoId)); }
        public int Delete(string collection, IEnumerable<BsonValue> ids)
        { return Call(() => _inner.Delete(collection, ids)); }
        public int DeleteMany(string collection, BsonExpression predicate)
        { return Call(() => _inner.DeleteMany(collection, predicate)); }
        public bool DropCollection(string name)
        { return Call(() => _inner.DropCollection(name)); }
        public bool RenameCollection(string name, string newName)
        { return Call(() => _inner.RenameCollection(name, newName)); }
        public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique)
        { return Call(() => _inner.EnsureIndex(collection, name, expression, unique)); }
        public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, VectorIndexOptions options)
        { return Call(() => _inner.EnsureVectorIndex(collection, name, expression, options)); }
        public bool DropIndex(string collection, string name)
        { return Call(() => _inner.DropIndex(collection, name)); }
        public BsonValue Pragma(string name)
        { return Call(() => _inner.Pragma(name)); }
        public bool Pragma(string name, BsonValue value)
        { return Call(() => _inner.Pragma(name, value)); }
        public void Dispose() => TransactionContext.Unbound(() => { _inner.Dispose(); return true; });

        private sealed class Reader : IBsonDataReader
        {
            private readonly IBsonDataReader _inner;
            internal Reader(IBsonDataReader inner) { _inner = inner; }
            public BsonValue this[string field] => TransactionContext.Unbound(() => _inner[field]);
            public string Collection => TransactionContext.Unbound(() => _inner.Collection);
            public BsonValue Current => TransactionContext.Unbound(() => _inner.Current);
            public bool HasValues => TransactionContext.Unbound(() => _inner.HasValues);
            public bool Read() => TransactionContext.Unbound(() => _inner.Read());
            public void Dispose() => TransactionContext.Unbound(() => { _inner.Dispose(); return true; });
        }
    }
}
