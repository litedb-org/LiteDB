using System;
using System.Collections.Generic;
using LiteDB.Engine;
using LiteDB.Vector;
namespace LiteDB.Tests.Engine
{
    internal sealed class DispatchForwardingEngine : ILiteEngine
    {
        private readonly ILiteEngine _inner;
        public Action BeforeUpdate { get; set; }
        public DispatchForwardingEngine(ILiteEngine inner) => _inner = inner;
        public int Checkpoint() => _inner.Checkpoint();
        public long Rebuild(RebuildOptions options) => _inner.Rebuild(options);
        public bool BeginTrans() => _inner.BeginTrans();
        public bool Commit() => _inner.Commit();
        public bool Rollback() => _inner.Rollback();
        public IBsonDataReader Query(string collection, Query query) => _inner.Query(collection, query);
        public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => _inner.Insert(collection, docs, autoId);
        public int Update(string collection, IEnumerable<BsonDocument> docs)
        {
            var before = BeforeUpdate;
            BeforeUpdate = null;
            before?.Invoke();
            return _inner.Update(collection, docs);
        }
        public int UpdateMany(string collection, BsonExpression transform, BsonExpression predicate) => _inner.UpdateMany(collection, transform, predicate);
        public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => _inner.Upsert(collection, docs, autoId);
        public int Delete(string collection, IEnumerable<BsonValue> ids) => _inner.Delete(collection, ids);
        public int DeleteMany(string collection, BsonExpression predicate) => _inner.DeleteMany(collection, predicate);
        public bool DropCollection(string name) => _inner.DropCollection(name);
        public bool RenameCollection(string name, string newName) => _inner.RenameCollection(name, newName);
        public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique) => _inner.EnsureIndex(collection, name, expression, unique);
        public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, VectorIndexOptions options) => _inner.EnsureVectorIndex(collection, name, expression, options);
        public bool DropIndex(string collection, string name) => _inner.DropIndex(collection, name);
        public BsonValue Pragma(string name) => _inner.Pragma(name);
        public bool Pragma(string name, BsonValue value) => _inner.Pragma(name, value);
        public void Dispose() => _inner.Dispose();
    }

}
