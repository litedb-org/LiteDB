#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Vector;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileDecoratorOwnership_Tests
    {
        private sealed class ForwardingEngine : ILiteEngine
        {
            private readonly ILiteEngine _inner;
            internal ForwardingEngine(ILiteEngine inner) => _inner = inner;
            public bool BeginTrans() => _inner.BeginTrans();
            public bool Commit() => _inner.Commit();
            public bool Rollback() => _inner.Rollback();
            public int Checkpoint() => _inner.Checkpoint();
            public long Rebuild(RebuildOptions options) => _inner.Rebuild(options);
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
            public void Dispose() => _inner.Dispose();
        }

        [OneFileTheory]
        [InlineData(false, false)] [InlineData(false, true)]
        [InlineData(true, false)] [InlineData(true, true)]
        public void Transparent_decorators_preserve_false_joins_implicit_abort_and_foreign_completion(bool encrypted, bool pinned)
        {
            using var files = new SharedOneFilePublicationFixture(encrypted ? "secret" : null);
            using var engine = new SharedEngine(files.Settings);
            using var other = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(new ForwardingEngine(engine), disposeOnClose: false);
            using var peer = new LiteDatabase(new ForwardingEngine(other), disposeOnClose: false);
            engine.PinIdleLimit = engine.PinHoldLimit = TimeSpan.FromMinutes(10);
            using var anchor = pinned ? engine.Query("docs", new Query()) : null;
            if (anchor != null) { anchor.Read().Should().BeTrue(); engine.Update("docs", new[] { Row(1, 0) }); }
            db.BeginTrans().Should().BeTrue(); db.BeginTrans().Should().BeFalse();
            db.GetCollection("docs").Update(Row(1, 99));
            Action duplicate = () => db.GetCollection("docs").Insert(Row(2, 9));
            duplicate.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(110);
            peer.BeginTrans().Should().BeTrue(); peer.Rollback().Should().BeTrue();
            db.BeginTrans().Should().BeTrue(); db.GetCollection("docs").Update(Row(1, 1));
            Exception foreign = null;
            var thread = new Thread(() => foreign = Record.Exception(() => db.Commit())) { IsBackground = true };
            thread.Start(); thread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            foreign.Should().BeOfType<LiteException>().Which.Message.Should().Contain("same thread");
            db.Commit().Should().BeTrue(); engine.Dispose(); other.Dispose(); anchor?.Dispose();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var cold = new LiteDatabase(new ConnectionString { Filename = files.Filename, Password = files.Settings.Password });
                Verify(cold, 1);
            }
        }
    }
}
#endif
