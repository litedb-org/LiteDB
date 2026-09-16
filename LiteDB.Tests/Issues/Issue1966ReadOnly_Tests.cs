using System;
using System.IO;
using LiteDB.Vector;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1966ReadOnly_Tests
    {
        [Fact]
        public void Disposed_readonly_engine_keeps_lifecycle_error_precedence()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
                setup.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, ReadOnly = true });
            engine.Dispose();
            var scalar = Assert.Throws<LiteException>(() => engine.EnsureIndex("rows", "value", "$.value", false));
            var vector = Assert.Throws<LiteException>(() => engine.EnsureVectorIndex("rows", "vector", "$.embedding", new VectorIndexOptions(2)));
            Assert.Equal(LiteException.EngineDisposed().ErrorCode, scalar.ErrorCode);
            Assert.Equal(scalar.ErrorCode, vector.ErrorCode);
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Rejected_scalar_and_vector_indexes_leave_connection_readable(ConnectionType mode)
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
            {
                setup.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 4 });
            }
            var before = File.ReadAllBytes(file.Filename);
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true, Connection = mode }))
            {
                var rows = db.GetCollection("rows");
                for (var i = 0; i < 2; i++)
                {
                    Assert.Throws<NotSupportedException>(() => rows.EnsureIndex("value"));
                    Assert.Equal(4, rows.FindById(1)["value"].AsInt32);
                    Assert.Throws<NotSupportedException>(() => rows.EnsureIndex("vector", "$.embedding", new VectorIndexOptions(2)));
                    Assert.Equal(1, rows.Count());
                }
            }
            Assert.Equal(before, File.ReadAllBytes(file.Filename));
        }
            [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Ensuring_an_existing_index_is_a_readonly_no_op(ConnectionType mode)
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
            {
                var rows = setup.GetCollection("rows");
                rows.EnsureIndex("value");
                rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 4 });
            }
            var before = File.ReadAllBytes(file.Filename);
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true, Connection = mode }))
            {
                var rows = db.GetCollection("rows");
                Assert.False(rows.EnsureIndex("value"));
                Assert.Throws<LiteException>(() => rows.EnsureIndex("value", "$.other"));
                Assert.Throws<NotSupportedException>(() => rows.EnsureIndex("missing"));
                Assert.Throws<NotSupportedException>(() => db.GetCollection("absent").EnsureIndex("value"));
                Assert.Equal(1, rows.Count(Query.EQ("value", 4)));
            }
            Assert.Equal(before, File.ReadAllBytes(file.Filename));
        }
            [Fact]
        public void Ensuring_an_existing_vector_index_readonly_reports_the_same_conflicts_as_a_writable_database()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
            {
                var rows = setup.GetCollection("rows");
                rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 4, ["embedding"] = new BsonArray { 1f, 0f } });
                rows.EnsureIndex("scalar", "$.embedding");
                rows.EnsureIndex("vector", "$.embedding", new VectorIndexOptions(2));
            }
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true }))
            {
                var rows = db.GetCollection("rows");
                Assert.False(rows.EnsureIndex("vector", "$.embedding", new VectorIndexOptions(2)));
                Assert.Throws<LiteException>(() => rows.EnsureIndex("vector", "$.embedding", new VectorIndexOptions(3)));
                Assert.Throws<LiteException>(() => rows.EnsureIndex("scalar", "$.embedding", new VectorIndexOptions(2)));
                Assert.Throws<NotSupportedException>(() => rows.EnsureIndex("missing", "$.embedding", new VectorIndexOptions(2)));
            }
        }
    }
}
