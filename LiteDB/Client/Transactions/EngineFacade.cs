using System;
using System.Collections.Generic;
using LiteDB.Engine;
using LiteDB.Vector;

namespace LiteDB
{
    /// <summary>One dispatch boundary for all existing engine operations.</summary>
    internal abstract class EngineFacade : ILiteEngine
    {
        private readonly ILiteEngine _inner;
        protected virtual ILiteEngine Inner => _inner;
        protected EngineFacade(ILiteEngine inner) { _inner = inner; }
        protected abstract T Invoke<T>(Func<T> action);
        public virtual IBsonDataReader Query(string collection, Query query) => Invoke(() => Inner.Query(collection, query));
        public virtual bool BeginTrans() => Invoke(Inner.BeginTrans);
        public virtual bool Commit() => Invoke(Inner.Commit);
        public virtual bool Rollback() => Invoke(Inner.Rollback);
        public virtual int Checkpoint() => Invoke(Inner.Checkpoint);
        public virtual long Rebuild(RebuildOptions options) => Invoke(() => Inner.Rebuild(options));
        public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => Invoke(() => Inner.Insert(collection, docs, autoId));
        public int Update(string collection, IEnumerable<BsonDocument> docs) => Invoke(() => Inner.Update(collection, docs));
        public int UpdateMany(string collection, BsonExpression transform, BsonExpression predicate) => Invoke(() => Inner.UpdateMany(collection, transform, predicate));
        public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => Invoke(() => Inner.Upsert(collection, docs, autoId));
        public int Delete(string collection, IEnumerable<BsonValue> ids) => Invoke(() => Inner.Delete(collection, ids));
        public int DeleteMany(string collection, BsonExpression predicate) => Invoke(() => Inner.DeleteMany(collection, predicate));
        public bool DropCollection(string name) => Invoke(() => Inner.DropCollection(name));
        public bool RenameCollection(string name, string newName) => Invoke(() => Inner.RenameCollection(name, newName));
        public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique) => Invoke(() => Inner.EnsureIndex(collection, name, expression, unique));
        public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, VectorIndexOptions options) => Invoke(() => Inner.EnsureVectorIndex(collection, name, expression, options));
        public bool DropIndex(string collection, string name) => Invoke(() => Inner.DropIndex(collection, name));
        public BsonValue Pragma(string name) => Invoke(() => Inner.Pragma(name));
        public virtual bool Pragma(string name, BsonValue value) => Invoke(() => Inner.Pragma(name, value));
        public abstract void Dispose();
    }
}
