using System;
using System.Collections.Generic;
using LiteDB;
using LiteDB.Engine;
using LiteDB.Vector;

/// <summary>
/// An application's own <see cref="ILiteEngine"/> decorator written against the pre-handle
/// interface. Loading it against the candidate proves that no abstract member was added to
/// <see cref="ILiteEngine"/>; a <c>LiteDatabase</c> over it must keep legacy transactions working
/// and refuse handle begins before side effects.
/// </summary>
public sealed class LegacyEngineDecorator : ILiteEngine
{
    private readonly ILiteEngine _inner;

    public LegacyEngineDecorator(ILiteEngine inner) { _inner = inner; }

    /// <summary>Set when a facade disposed this engine; must stay false with <c>disposeOnClose: false</c>.</summary>
    public bool Disposed { get; private set; }

    public int Checkpoint() => _inner.Checkpoint();
    public long Rebuild(RebuildOptions options) => _inner.Rebuild(options);
    public bool BeginTrans() => _inner.BeginTrans();
    public bool Commit() => _inner.Commit();
    public bool Rollback() => _inner.Rollback();
    public IBsonDataReader Query(string collection, Query query) => _inner.Query(collection, query);
    public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => _inner.Insert(collection, docs, autoId);
    public int Update(string collection, IEnumerable<BsonDocument> docs) => _inner.Update(collection, docs);
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

    public void Dispose()
    {
        this.Disposed = true;
        _inner.Dispose();
    }
}
