using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class IndexMigrationCapacity_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData("capacity-password", false)]
        [InlineData(null, true)]
        [InlineData("capacity-password", true)]
        public void Tight_limit_rejects_before_promotion_and_explicit_limit_recovers_pending_files(string password, bool pending)
        {
            using var file = new TempFile();
            using (var db = IndexMigration_Tests.Open(file.Filename, password))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(Enumerable.Range(1, 2000).Select(i => new BsonDocument
                {
                    ["_id"] = i, ["value"] = new string('X', 200) + i
                }));
                rows.EnsureIndex("computed", "LOWER($.value)");
                db.Checkpoint();
                db.LimitSize = new FileInfo(file.Filename).Length;
            }
            IndexMigration_Tests.RewriteHeaders(file.Filename, password, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = (byte)(pending ? 10 : 8);
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
            });
            var before = File.ReadAllBytes(file.Filename);
            Action open = () => { using var db = IndexMigration_Tests.Open(file.Filename, password); };
            open.Should().Throw<LiteException>().WithMessage("*capacity*IndexMigrationLimitSize*");
            File.ReadAllBytes(file.Filename).Should().Equal(before);

            var settings = new ConnectionString
            {
                Filename = file.Filename, Password = password, IndexMigrationLimitSize = 8 * 1024 * 1024
            };
            using (var db = new LiteDatabase(new ConnectionString(settings.ToStringWithPassword())))
            {
                db.LimitSize.Should().Be(settings.IndexMigrationLimitSize.Value);
                db.GetCollection("rows").Count().Should().Be(2000);
                db.GetCollection("rows").Count("LOWER($.value) = @0", new string('x', 200) + "27").Should().Be(1);
                db.Checkpoint();
            }
            using (var db = IndexMigration_Tests.Open(file.Filename, password, true))
                db.LimitSize.Should().Be(settings.IndexMigrationLimitSize.Value);
            // Retaining the old computed-index pages avoids the previous extra
            // full copy. Allow one page for random skip-list height variation.
            new FileInfo(file.Filename).Length.Should().BeLessThan(before.Length + 3 * Constants.PAGE_SIZE);
        }

        [Fact]
        public void Scalar_indexes_migrate_at_the_existing_size_limit()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "a" });
                db.GetCollection("rows").EnsureIndex("value");
                db.Checkpoint();
                db.LimitSize = new FileInfo(file.Filename).Length;
            }
            IndexMigration_Tests.RewriteHeaders(file.Filename, null, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = 8;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
            });
            var length = new FileInfo(file.Filename).Length;
            using (var db = new LiteDatabase(file.Filename))
            {
                db.LimitSize.Should().Be(length);
                db.GetCollection("rows").Count().Should().Be(1);
            }
            new FileInfo(file.Filename).Length.Should().Be(length);
        }

        [Fact]
        public void Limit_option_round_trips_and_cannot_reduce_the_stored_limit()
        {
            var settings = new ConnectionString("filename=:memory:;index migration limit size=8MB");
            settings.IndexMigrationLimitSize.Should().Be(8 * 1024 * 1024);
            new ConnectionString(settings.ToString()).IndexMigrationLimitSize.Should().Be(settings.IndexMigrationLimitSize);
            using var data = new MemoryStream();
            using (var db = new LiteDatabase(data)) db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            data.Position = EnginePragmas.P_INDEX_ORDER_VERSION;
            data.WriteByte(0);
            var before = data.ToArray();
            Action open = () => { using var engine = new LiteEngine(new EngineSettings { DataStream = data, IndexMigrationLimitSize = 8 * 1024 * 1024 }); };
            open.Should().Throw<ArgumentException>().WithMessage("*at least the stored LIMIT_SIZE*");
            data.ToArray().Should().Equal(before);
        }
    }
}
