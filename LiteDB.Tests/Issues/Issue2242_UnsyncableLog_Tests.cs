using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Issues
{
    /// <summary>
    /// #2242: some network shares and virtual file systems reject FlushFileBuffers/fsync.
    /// Such a log keeps working as before #2818: writes stay ordered in the OS cache, which
    /// survives a process crash, but no power-loss guarantee is claimed.
    /// </summary>
    public class Issue2242_UnsyncableLog_Tests
    {
        [Fact]
        public void New_database_on_a_log_that_never_syncs_writes_checkpoints_and_reopens()
        {
            using var data = new MemoryStream();
            using var log = new UnsyncableLog();

            using (var db = Open(data, log))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(Documents(0, 16));
                rows.EnsureIndex("value");
                db.Checkpoint();
                rows.Insert(Documents(16, 16));
                db.Checkpoint();
                rows.Update(Documents(0, 32, value: 1));

                DurableLogFlush(db).Should().BeFalse("the weaker guarantee must be discoverable");
                log.Rejections.Should().BeGreaterThan(0);
            }

            AssertDocuments(data, log, count: 32, value: 1);
        }

        [Theory]
        [InlineData(8)]
        [InlineData(9)]
        public void Legacy_database_on_a_log_that_never_syncs_converts_and_keeps_writing(byte version)
        {
            using var source = new WalTestDatabase(password: null);
            source.Seed("rows");
            source.Database.GetCollection("rows").EnsureIndex("value");
            source.Database.Checkpoint();
            using var data = ChecksumTestFiles.Copy(source.Data.ToArray());
            using var log = new UnsyncableLog();
            ChecksumTestFiles.MakeLegacy(data, log, password: null, version);

            using (var db = Open(data, log))
            {
                db.GetCollection("$database").FindAll().Single()["checksumCoverage"].AsString.Should().Be("Mixed");
                db.GetCollection("rows").UpdateMany("{ value: 1 }", "true");
                db.Checkpoint();
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 100, ["value"] = 1 });
            }

            AssertDocuments(data, log, count: WalTestDatabase.DocumentCount + 1, value: 1);
        }

        [Fact]
        public void Compact_promotion_on_a_log_that_never_syncs_publishes_v12_and_keeps_writing()
        {
            using var data = new MemoryStream();
            using var log = new UnsyncableLog();

            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Legacy }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("rows").Insert(Documents(0, 16));
                db.Checkpoint();
            }
            data.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.INDEX_FILE_VERSION);

            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Compact }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                // Repeated field names make the compact representation beneficial.
                rows.Insert(Enumerable.Range(16, 16).Select(id =>
                {
                    var document = LiteDB.Tests.Engine.CompactStorage_Tests.Document(id);
                    document["value"] = 1;
                    return document;
                }));
                db.Checkpoint();
                rows.Update(Documents(0, 16, value: 1));
                DurableLogFlush(db).Should().BeFalse();
            }

            data.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.COMPACT_FILE_VERSION, "the first compact write promotes v11");
            AssertDocuments(data, log, count: 32, value: 1);
        }

        // Reopen a copy of what a killed process leaves behind: every byte handed to the OS.
        private static void AssertDocuments(MemoryStream data, MemoryStream log, int count, int value)
        {
            using var dataCopy = ChecksumTestFiles.Copy(data.ToArray());
            using var logCopy = new UnsyncableLog();
            var logBytes = log.ToArray();
            logCopy.Write(logBytes, 0, logBytes.Length);
            logCopy.Position = 0;

            using var db = Open(dataCopy, logCopy);
            var rows = db.GetCollection("rows");
            rows.Count().Should().Be(count);
            rows.Find(Query.EQ("value", value)).Should().HaveCount(count);
            rows.Insert(new BsonDocument { ["_id"] = -1, ["value"] = value });
            db.Checkpoint();
            rows.Count().Should().Be(count + 1);
        }

        private static LiteDatabase Open(Stream data, Stream log)
        {
            var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            var db = new LiteDatabase(engine);
            db.CheckpointSize = 0;
            return db;
        }

        private static BsonDocument[] Documents(int first, int count, int value = 0) =>
            Enumerable.Range(first, count).Select(id => new BsonDocument
            {
                ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1500)
            }).ToArray();

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

        /// <summary>A log whose storage answers every device sync with ERROR_ACCESS_DENIED.</summary>
        private sealed class UnsyncableLog : MemoryStream, IDurableStream
        {
            internal int Rejections;

            public void FlushToDisk()
            {
                Rejections++;
                throw new UnauthorizedAccessException("Access to the path is denied.");
            }
        }
    }
}
