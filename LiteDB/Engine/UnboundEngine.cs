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
        internal UnboundEngine(ILiteEngine inner) { _inner = inner; }
        public int Checkpoint()
        { return TransactionContext.Unbound(() => _inner.Checkpoint()); }
        public long Rebuild(RebuildOptions options)
        { return TransactionContext.Unbound(() => _inner.Rebuild(options)); }
        public bool BeginTrans()
        { return TransactionContext.Unbound(() => _inner.BeginTrans()); }
        public bool Commit()
        { return TransactionContext.Unbound(() => _inner.Commit()); }
        public bool Rollback()
        { return TransactionContext.Unbound(() => _inner.Rollback()); }
        public IBsonDataReader Query(string collection, Query query)
        { return new Reader(TransactionContext.Unbound(() => _inner.Query(collection, query))); }
        public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        { return TransactionContext.Unbound(() => _inner.Insert(collection, docs, autoId)); }
        public int Update(string collection, IEnumerable<BsonDocument> docs)
        { return TransactionContext.Unbound(() => _inner.Update(collection, docs)); }
        public int UpdateMany(string collection, BsonExpression transform, BsonExpression predicate)
        { return TransactionContext.Unbound(() => _inner.UpdateMany(collection, transform, predicate)); }
        public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        { return TransactionContext.Unbound(() => _inner.Upsert(collection, docs, autoId)); }
        public int Delete(string collection, IEnumerable<BsonValue> ids)
        { return TransactionContext.Unbound(() => _inner.Delete(collection, ids)); }
        public int DeleteMany(string collection, BsonExpression predicate)
        { return TransactionContext.Unbound(() => _inner.DeleteMany(collection, predicate)); }
        public bool DropCollection(string name)
        { return TransactionContext.Unbound(() => _inner.DropCollection(name)); }
        public bool RenameCollection(string name, string newName)
        { return TransactionContext.Unbound(() => _inner.RenameCollection(name, newName)); }
        public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique)
        { return TransactionContext.Unbound(() => _inner.EnsureIndex(collection, name, expression, unique)); }
        public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, VectorIndexOptions options)
        { return TransactionContext.Unbound(() => _inner.EnsureVectorIndex(collection, name, expression, options)); }
        public bool DropIndex(string collection, string name)
        { return TransactionContext.Unbound(() => _inner.DropIndex(collection, name)); }
        public BsonValue Pragma(string name)
        { return TransactionContext.Unbound(() => _inner.Pragma(name)); }
        public bool Pragma(string name, BsonValue value)
        { return TransactionContext.Unbound(() => _inner.Pragma(name, value)); }
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
