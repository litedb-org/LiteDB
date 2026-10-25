using System;
using System.Collections.Generic;
using System.IO;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1344Decorator_Tests
    {
        private sealed class ForwardingEngine : ILiteEngine
        {
            private readonly ILiteEngine _inner;
            public Action BeforeUpdate { get; set; }
            public ForwardingEngine(ILiteEngine inner) => _inner = inner;
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

        [Fact]
        public void Deletion_does_not_recreate_a_collection_dropped_after_the_existence_check()
        {
            var engine = new ForwardingEngine(new LiteEngine(new EngineSettings { Filename = ":memory:" }));
            using var db = new LiteDatabase(engine);
            using (var source = new MemoryStream(new byte[] { 1 })) db.FileStorage.Upload("target", "target", source);
            engine.BeforeUpdate = () => Assert.True(engine.DropCollection("_files"));
            Assert.False(db.FileStorage.Delete("target"));
            Assert.DoesNotContain("_files", db.GetCollectionNames());
        }

        [Fact]
        public void Deletion_joins_a_caller_transaction_through_an_engine_decorator()
        {
            using var db = new LiteDatabase(new ForwardingEngine(new LiteEngine(new EngineSettings { Filename = ":memory:" })));
            using (var source = new MemoryStream(new byte[] { 1, 2 })) db.FileStorage.Upload("target", "old", source);
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });
            rows.Insert(new BsonDocument { ["_id"] = 2 });
            Assert.True(db.BeginTrans());
            using (var cursor = rows.FindAll().GetEnumerator())
            {
                Assert.True(cursor.MoveNext());
                Assert.True(db.FileStorage.Delete("target"));
                Assert.True(cursor.MoveNext());
                // Storage writes must not change the public BeginTrans cursor guard.
                Assert.Throws<LiteException>(() => db.BeginTrans());
            }
            Assert.True(db.Rollback());
            using var downloaded = new MemoryStream();
            db.FileStorage.Download("target", downloaded);
            Assert.Equal(new byte[] { 1, 2 }, downloaded.ToArray());
        }
    }
}
