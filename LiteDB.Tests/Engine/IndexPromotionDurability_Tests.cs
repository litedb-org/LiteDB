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

        /// <summary>
        /// #2242 opted out of durable commits: the promotion overwrites the header in place behind its
        /// header journal, so a log that cannot sync refuses it before the journal is written, as with
        /// durable commits. It used to publish the new format in write order behind a journal in the OS
        /// cache only, which a power loss mid-overwrite could leave torn with no durable repair (external
        /// review, point 1); decision D keeps the recovery rule for callers that opted out. The refusal
        /// is tagged as unsynced storage, so the open falls back to read-only (decision 2): the row
        /// reads, $database says why and records no failure ("cannot sync" is the reason to opt out,
        /// proposed default A), a write throws naming the refusal, and both files stay byte for byte,
        /// the format unchanged. Once the log syncs, the next open publishes it.
        /// </summary>
        [Fact]
        public void Unsupported_journal_sync_opens_read_only_without_publishing_the_new_format_without_durable_commits()
        {
            using var data = LegacyIndexOrdering(password: null, out var originalLog);
            var before = data.ToArray();
            using var log = new SyncFailureStream(originalLog, new UnauthorizedAccessException("Durable sync unsupported"));
            using (var db = Open(data, log, password: null, durableCommits: false))
            {
                var info = WriteFailureAssert.Info(db);
                info["readOnly"].AsBoolean.Should().BeTrue();
                info["durableLogFlush"].AsBoolean.Should().BeFalse();
                var reason = info["readOnlyReason"].AsString;
                reason.Should().StartWith(WriteFailureAssert.LogCannotSync + "an overwrite of the data file writes nothing");
                WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");
                db.GetCollection("rows").FindAll().Should().Equal(new BsonDocument { ["_id"] = 1, ["payload"] = "acknowledged" });
                db.GetCollection("rows").FindById(1)["payload"].AsString.Should().Be("acknowledged");

                Action write = () => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                write.Should().Throw<IOException>().Which.Message.Should().Be(WriteFailureAssert.OpenRefused + reason);
                db.GetCollection("rows").Count().Should().Be(1);
            }
            data.ToArray().Should().Equal(before, "the promotion was refused before it wrote");
            log.ToArray().Should().Equal(originalLog);
            data.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.CHECKSUM_FILE_VERSION);

            AssertRecovered(data, log, password: null); // on a log that syncs
            data.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.INDEX_FILE_VERSION, "the promotion goes through once the log syncs");
        }

        /// <summary>
        /// With durable commits (the default) the promotion's journal sync is a recovery barrier on a
        /// log that cannot sync: it is refused before the data file changes (decision 3), and the open
        /// falls back to read-only (decision 2). The row reads, $database says why, a write throws
        /// naming the refusal, and neither file changes; the format is published once the log syncs.
        /// </summary>
        [Fact]
        public void Unsupported_journal_sync_opens_read_only_without_publishing_the_new_format_with_durable_commits()
        {
            using var data = LegacyIndexOrdering(password: null, out var originalLog);
            var before = data.ToArray();
            using var log = new SyncFailureStream(originalLog, new UnauthorizedAccessException("Durable sync unsupported"));
            using (var db = Open(data, log, password: null))
            {
                var info = WriteFailureAssert.Info(db);
                info["readOnly"].AsBoolean.Should().BeTrue();
                var reason = info["readOnlyReason"].AsString;
                reason.Should().StartWith(WriteFailureAssert.LogCannotSync);
                db.GetCollection("rows").FindAll().Should().Equal(new BsonDocument { ["_id"] = 1, ["payload"] = "acknowledged" });

                Action write = () => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                write.Should().Throw<IOException>().Which.Message.Should().Be(WriteFailureAssert.OpenRefused + reason);
                db.GetCollection("rows").Count().Should().Be(1);
            }
            data.ToArray().Should().Equal(before, "the promotion was refused before it wrote");
            log.ToArray().Should().Equal(originalLog);
            data.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.CHECKSUM_FILE_VERSION);
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

        private static LiteDatabase Open(Stream data, Stream log, string password, bool durableCommits = true) =>
            new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password, DurableCommits = durableCommits }));

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
