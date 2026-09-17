using System.Collections.Generic;

using LiteDB.Engine;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.AotTests;

internal sealed class RecordingEngine : ILiteEngine
{
    public int DataAccessCount { get; private set; }

    public IBsonDataReader Query(string collection, Query query) => Access<IBsonDataReader>();
    public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => Access<int>();
    public int Update(string collection, IEnumerable<BsonDocument> docs) => Access<int>();
    public int UpdateMany(string collection, BsonExpression transform, BsonExpression predicate) => Access<int>();
    public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId) => Access<int>();
    public int Delete(string collection, IEnumerable<BsonValue> ids) => Access<int>();
    public int DeleteMany(string collection, BsonExpression predicate) => Access<int>();
    public int Checkpoint() => Access<int>();
    public long Rebuild(RebuildOptions options) => Access<long>();
    public bool BeginTrans() => Access<bool>();
    public bool Commit() => Access<bool>();
    public bool Rollback() => Access<bool>();
    public bool DropCollection(string name) => Access<bool>();
    public bool RenameCollection(string name, string newName) => Access<bool>();
    public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique) => Access<bool>();
    public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, Vector.VectorIndexOptions options) => Access<bool>();
    public bool DropIndex(string collection, string name) => Access<bool>();
    public BsonValue Pragma(string name) => Access<BsonValue>();
    public bool Pragma(string name, BsonValue value) => Access<bool>();

    public void Dispose()
    {
    }

    private T Access<T>()
    {
        DataAccessCount++;
        throw new AssertFailedException("Generated configuration validation must run before engine access.");
    }
}
