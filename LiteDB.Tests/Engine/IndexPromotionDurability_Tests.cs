using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class IndexPromotionDurability_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Unsupported_journal_sync_cannot_publish_a_new_file_format(string password)
        {
            using var data = new MemoryStream();
            using var originalLog = new MemoryStream();
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings
                { DataStream = data, LogStream = originalLog, Password = password })))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "acknowledged" });
                db.Checkpoint();
            }
            IndexMigrationFixtures.Rewrite(data, originalLog, password, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = HeaderPage.CHECKSUM_FILE_VERSION;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
            });
            var before = data.ToArray();
            using var log = new UnsupportedSyncStream(originalLog.ToArray());
            Action open = () => { using var db = new LiteDatabase(new LiteEngine(new EngineSettings
                { DataStream = data, LogStream = log, Password = password })); };
            open.Should().Throw<UnauthorizedAccessException>();
            data.ToArray().Should().Equal(before, "format publication requires a successful durable journal sync");
            using var recoverableLog = ChecksumTestFiles.Copy(log.ToArray());
            using var recovered = new LiteDatabase(new LiteEngine(new EngineSettings
                { DataStream = data, LogStream = recoverableLog, Password = password }));
            recovered.GetCollection("rows").FindById(1)["payload"].AsString.Should().Be("acknowledged");
        }

        private sealed class UnsupportedSyncStream : MemoryStream, IDurableStream
        {
            internal UnsupportedSyncStream(byte[] bytes) { Write(bytes, 0, bytes.Length); Position = 0; }
            public void FlushToDisk() => throw new UnauthorizedAccessException("Durable sync unsupported");
        }
    }
}
