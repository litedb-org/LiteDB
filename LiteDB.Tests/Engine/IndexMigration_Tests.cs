using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class IndexMigration_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData("migration-password", false)]
        [InlineData(null, true)]
        [InlineData("migration-password", true)]
        public void Legacy_indexes_rebuild_from_documents_and_preserve_settings(string password, bool wal)
        {
            using var file = new TempFile();
            using (var db = Open(file.Filename, password))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(Enumerable.Range(1, 300).Select(i => new BsonDocument
                {
                    ["_id"] = i, ["key"] = 301 - i,
                    ["v"] = new BsonVector(new[] { (float)i, 1f })
                }));
                rows.EnsureIndex("key", true);
                rows.EnsureIndex("vector", "$.v", new VectorIndexOptions(2));
                rows.EnsureIndex("computedVector", "COALESCE($.v, [0, 1])", new VectorIndexOptions(2));
                rows.EnsureIndex("computed", "$.key + 0");
                db.UserVersion = 42;
                if (wal) db.CheckpointSize = 0;
            }
            RewriteHeaders(file.Filename, password, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = 9;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
            });
            using (var db = Open(file.Filename, password))
            {
                db.UserVersion.Should().Be(42);
                var rows = db.GetCollection("rows");
                rows.Count().Should().Be(300);
                rows.Find(Query.EQ("key", 299)).Single()["_id"].AsInt32.Should().Be(2);
                rows.Query().OrderBy("$.key").ToArray().Select(x => x["key"].AsInt32)
                    .Should().Equal(Enumerable.Range(1, 300));
                rows.Query().TopKNear("v", new[] { 1f, 1f }, 1).ToArray().Should().ContainSingle();
                rows.Query().TopKNear(BsonExpression.Create("COALESCE($.v, [0, 1])"), new[] { 1f, 1f }, 1).ToArray().Should().ContainSingle();
                rows.Update(new BsonDocument { ["_id"] = 2, ["key"] = 999, ["v"] = new BsonVector(new[] { 2f, 1f }) });
                rows.Find("$.key + 0 = 999").Should().ContainSingle();
                rows.Delete(3).Should().BeTrue();
                db.Checkpoint();
            }
            using (var db = Open(file.Filename, password, true)) db.GetCollection("rows").Count().Should().Be(299);
            ReadHeader(file.Filename, password)[HeaderPage.P_FILE_VERSION].Should().Be(10);
            ReadHeader(file.Filename, password)[EnginePragmas.P_INDEX_ORDER_VERSION].Should().Be(1);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("migration-password")]
        public void Legacy_read_only_open_requests_writable_migration_without_changing_bytes(string password)
        {
            using var file = new TempFile();
            using (var db = Open(file.Filename, password)) db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            RewriteHeaders(file.Filename, password, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = 8;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
            });
            var before = File.ReadAllBytes(file.Filename);
            Action open = () => { using var db = Open(file.Filename, password, true); };
            open.Should().Throw<LiteException>().WithMessage("*writable*rebuild*");
            File.ReadAllBytes(file.Filename).Should().Equal(before);
            using (var db = Open(file.Filename, password)) db.GetCollection("rows").Count().Should().Be(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Promotion_interrupted_before_migration_resumes_with_old_wal_headers(bool wal)
        {
            using var file = new TempFile();
            using (var db = Open(file.Filename, null))
            {
                if (wal) db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            }
            RewriteHeaders(file.Filename, null, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = 8;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
            });
            using (var stream = File.Open(file.Filename, FileMode.Open, FileAccess.Write))
            {
                stream.Position = HeaderPage.P_FILE_VERSION;
                stream.WriteByte(10);
            }
            using (var db = Open(file.Filename, null))
            {
                Assert.NotNull(db.GetCollection("rows").FindById(1));
                db.Checkpoint();
            }
            ReadHeader(file.Filename, null)[HeaderPage.P_FILE_VERSION].Should().Be(10);
            ReadHeader(file.Filename, null)[EnginePragmas.P_INDEX_ORDER_VERSION].Should().Be(1);
        }

        internal static LiteDatabase Open(string file, string password, bool readOnly = false) =>
            new LiteDatabase(new LiteEngine(new EngineSettings
            {
                Filename = file, Password = password, ReadOnly = readOnly, TransactionPageLimit = 8
            }));

        internal static byte[] ReadHeader(string file, string password)
        {
            using var factory = new FileStreamFactory(file, password, true, false);
            using var stream = factory.GetStream(false, true);
            var header = new byte[Constants.PAGE_SIZE];
            stream.Read(header, 0, header.Length).Should().Be(header.Length);
            return header;
        }

        internal static void RewriteHeaders(string file, string password, Action<byte[]> change)
        {
            foreach (var path in new[] { file, FileHelper.GetLogFile(file) })
            {
                if (!File.Exists(path)) continue;
                using var factory = new FileStreamFactory(path, password, false, false);
                using var stream = factory.GetStream(true, path != file);
                var header = new byte[Constants.PAGE_SIZE];
                for (long offset = 0; offset < stream.Length; offset += header.Length)
                {
                    stream.Position = offset;
                    if (stream.Read(header, 0, header.Length) != header.Length) break;
                    if (header[BasePage.P_PAGE_TYPE] != (byte)PageType.Header) continue;
                    change(header);
                    stream.Position = offset;
                    stream.Write(header, 0, header.Length);
                }
                stream.FlushToDisk();
            }
        }
    }
}
