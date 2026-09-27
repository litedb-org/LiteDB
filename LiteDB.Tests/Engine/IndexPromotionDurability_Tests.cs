using System;
using System.IO;
using System.Linq;
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
        public void Failed_journal_sync_cannot_publish_a_new_file_format(string password)
        {
            using var data = LegacyIndexOrdering(password, out var originalLog);
            var before = data.ToArray();
            using var log = new SyncFailureStream(originalLog, new IOException("durable sync failed"));
            Action open = () => { using var db = Open(data, log, password); };
            open.Should().Throw<IOException>().WithMessage("durable sync failed");
            data.ToArray().Should().Equal(before, "format publication requires a successful durable journal sync");
            AssertRecovered(data, log, password);
        }

        [Fact]
        public void Unsupported_journal_sync_publishes_the_new_format_without_a_power_loss_claim()
        {
            // #2242: storage that cannot sync keeps working as before #2818.
            using var data = LegacyIndexOrdering(password: null, out var originalLog);
            using var log = new SyncFailureStream(originalLog, new UnauthorizedAccessException("Durable sync unsupported"));
            using (var db = Open(data, log, password: null))
            {
                db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean.Should().BeFalse();
                db.GetCollection("rows").FindById(1)["payload"].AsString.Should().Be("acknowledged");
            }
            data.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.INDEX_FILE_VERSION);
            AssertRecovered(data, log, password: null);
        }

        [Fact]
        public void Unsupported_sync_still_rejects_an_encrypted_log_before_changing_the_data_file()
        {
            // The encrypted preamble requires a durable sync; dev rejected such storage too.
            using var data = LegacyIndexOrdering("secret", out var originalLog);
            var before = data.ToArray();
            using var log = new SyncFailureStream(originalLog, new UnauthorizedAccessException("Durable sync unsupported"));
            Action open = () => { using var db = Open(data, log, "secret"); };
            open.Should().Throw<UnauthorizedAccessException>();
            data.ToArray().Should().Equal(before);
            AssertRecovered(data, log, "secret");
        }

        private static MemoryStream LegacyIndexOrdering(string password, out byte[] log)
        {
            var data = new MemoryStream();
            using var originalLog = new MemoryStream();
            using (var db = Open(data, originalLog, password))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "acknowledged" });
                db.Checkpoint();
            }
            IndexMigrationFixtures.Rewrite(data, originalLog, password, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = HeaderPage.CHECKSUM_FILE_VERSION;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
            });
            log = originalLog.ToArray();
            return data;
        }

        private static void AssertRecovered(MemoryStream data, MemoryStream log, string password)
        {
            using var recoverableLog = ChecksumTestFiles.Copy(log.ToArray());
            using var recovered = Open(data, recoverableLog, password);
            recovered.GetCollection("rows").FindById(1)["payload"].AsString.Should().Be("acknowledged");
        }

        private static LiteDatabase Open(Stream data, Stream log, string password) =>
            new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }));

        private sealed class SyncFailureStream : MemoryStream, IDurableStream
        {
            private readonly Exception _failure;

            internal SyncFailureStream(byte[] bytes, Exception failure)
            {
                _failure = failure;
                Write(bytes, 0, bytes.Length);
                Position = 0;
            }

            public void FlushToDisk() => throw _failure;
        }
    }
}
