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
        private static TransactionContext.Scope Enter() => TransactionContext.Enter(null);
        public int Checkpoint()
        { using var call = Enter(); return _inner.Checkpoint(); }
        public long Rebuild(RebuildOptions options)
        { using var call = Enter(); return _inner.Rebuild(options); }
        public bool BeginTrans()
        { using var call = Enter(); return _inner.BeginTrans(); }
        public bool Commit()
        { using var call = Enter(); return _inner.Commit(); }
        public bool Rollback()
        { using var call = Enter(); return _inner.Rollback(); }
        public IBsonDataReader Query(string collection, Query query)
        { using var call = Enter(); return _inner.Query(collection, query); }
        public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        { using var call = Enter(); return _inner.Insert(collection, docs, autoId); }
        public int Update(string collection, IEnumerable<BsonDocument> docs)
        { using var call = Enter(); return _inner.Update(collection, docs); }
        public int UpdateMany(string collection, BsonExpression transform, BsonExpression predicate)
        { using var call = Enter(); return _inner.UpdateMany(collection, transform, predicate); }
        public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        { using var call = Enter(); return _inner.Upsert(collection, docs, autoId); }
        public int Delete(string collection, IEnumerable<BsonValue> ids)
        { using var call = Enter(); return _inner.Delete(collection, ids); }
        public int DeleteMany(string collection, BsonExpression predicate)
        { using var call = Enter(); return _inner.DeleteMany(collection, predicate); }
        public bool DropCollection(string name)
        { using var call = Enter(); return _inner.DropCollection(name); }
        public bool RenameCollection(string name, string newName)
        { using var call = Enter(); return _inner.RenameCollection(name, newName); }
        public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique)
        { using var call = Enter(); return _inner.EnsureIndex(collection, name, expression, unique); }
        public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, VectorIndexOptions options)
        { using var call = Enter(); return _inner.EnsureVectorIndex(collection, name, expression, options); }
        public bool DropIndex(string collection, string name)
        { using var call = Enter(); return _inner.DropIndex(collection, name); }
        public BsonValue Pragma(string name)
        { using var call = Enter(); return _inner.Pragma(name); }
        public bool Pragma(string name, BsonValue value)
        { using var call = Enter(); return _inner.Pragma(name, value); }
        public void Dispose() => _inner.Dispose();
    }
}
