using System;
using System.IO;
using System.Linq;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Audit2026
{
    public class EngineAuditRegression_Tests
    {
        private sealed class LongEntity
        {
            public int Id { get; set; }
            public long Value { get; set; }
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void C2_default_rebuild_preserves_encryption()
        {
            using var fixture = new TemporaryDirectory();
            var path = Path.Combine(fixture.Path, "encrypted.db");

            using (var db = new LiteDatabase($"Filename={path};Password=secret"))
            {
                db.GetCollection("items").Insert(new BsonDocument { ["value"] = "classified" });
                db.Rebuild();
                db.GetCollection("items").Count().Should().Be(1);
            }

            Action openWithoutPassword = () => new LiteDatabase(path).Dispose();
            openWithoutPassword.Should().Throw<LiteException>();

            using var reopened = new LiteDatabase($"Filename={path};Password=secret");
            reopened.GetCollection("items").Count().Should().Be(1);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void C7_max_value_does_not_resolve_to_index_sentinel()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection("items");
            col.Insert(new BsonDocument { ["_id"] = 1 });

            col.Delete(BsonValue.MaxValue).Should().BeFalse();
            col.Count().Should().Be(1);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H28_existing_non_unique_index_cannot_silently_ignore_unique_request()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection("items");
            col.EnsureIndex("email", "$.email", false);
            col.EnsureIndex("email", "$.email", true);
            col.Insert(new BsonDocument { ["email"] = "x@example.test" });

            Action duplicate = () => col.Insert(new BsonDocument { ["email"] = "x@example.test" });
            duplicate.Should().Throw<LiteException>();
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H31_ordinary_index_rejects_vector_keys_before_persisting_them()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection("items");
            col.Insert(new BsonDocument { ["vector"] = new float[] { 1, 2 } });

            Action createIndex = () => col.EnsureIndex("vector", "$.vector");
            createIndex.Should().Throw<LiteException>();
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H38_sort_round_trips_extended_string_keys()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection("items");
            col.Insert(new BsonDocument { ["value"] = new string('y', 300) });
            col.Insert(new BsonDocument { ["value"] = new string('x', 300) });

            col.Query().OrderBy("$.value").ToDocuments()
                .Select(x => x["value"].AsString[0]).Should().Equal('x', 'y');
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H48_mapper_widens_stored_int32_to_declared_int64()
        {
            var document = new BsonDocument { ["_id"] = 1, ["Value"] = 12 };
            BsonMapper.Global.Deserialize<LongEntity>(document).Value.Should().Be(12L);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H66_plain_sql_rebuild_uses_default_options()
        {
            using var fixture = new TemporaryDirectory();
            var path = Path.Combine(fixture.Path, "plain.db");
            using var db = new LiteDatabase(path);
            db.GetCollection("items").Insert(new BsonDocument { ["value"] = 1 });

            Action rebuild = () => db.Execute("REBUILD").Dispose();
            rebuild.Should().NotThrow();
            db.GetCollection("items").Count().Should().Be(1);
        }

        [Theory]
        [InlineData(":memory:")]
        [InlineData(":temp:")]
        [Trait("Category", "AuditBehavior")]
        public void H68_rebuild_of_sentinel_database_is_non_destructive(string connection)
        {
            using var db = new LiteDatabase(connection);
            db.GetCollection("items").Insert(new BsonDocument { ["value"] = 1 });

            db.Rebuild().Should().Be(0);
            db.GetCollection("items").Count().Should().Be(1);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M93_flushing_read_mode_file_stream_does_not_dereference_write_buffer()
        {
            using var db = new LiteDatabase(new MemoryStream());
            using (var writer = db.FileStorage.OpenWrite("file", "file"))
            {
                writer.WriteByte(1);
            }

            using var reader = db.FileStorage.OpenRead("file");
            Action flush = reader.Flush;
            flush.Should().NotThrow<NullReferenceException>();
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void M96_queryable_can_be_reused_after_into()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var source = db.GetCollection("source");
            source.Insert(new BsonDocument { ["value"] = 1 });
            source.Insert(new BsonDocument { ["value"] = 2 });
            var query = source.Query().Where("$.value > 0");

            query.Into("copy").Should().Be(2);
            query.Count().Should().Be(2);
            db.GetCollection("copy").Count().Should().Be(2);
        }

        private sealed class TemporaryDirectory : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "litedb-audit-" + Guid.NewGuid().ToString("N"));

            public TemporaryDirectory()
            {
                Directory.CreateDirectory(this.Path);
            }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(this.Path, true);
                }
                catch
                {
                    // A failing regression may leave a file handle open; temp cleanup is best effort.
                }
            }
        }
    }
}
