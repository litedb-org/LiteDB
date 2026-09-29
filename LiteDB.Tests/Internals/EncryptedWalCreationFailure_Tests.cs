using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Regressions;
using Xunit;

namespace LiteDB.Internals
{
    public class EncryptedWalCreationFailure_Tests
    {
        [Theory]
        [InlineData(1, false)]
        [InlineData(2, false)]
        [InlineData(3, false)]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(3, true)]
        public void FailedPreambleBarrier_StopsWritesAndPreservesRecoverableSources(int failAt, bool unsupported)
        {
            // A checksummed database with no physical WAL opens without creating
            // one. Its first new commit must establish the encrypted preamble.
            var original = EncryptedWalCreation_Tests.LegacyData(legacy: false);
            using var data = ChecksumTestFiles.Copy(original);
            using var log = new FailedSyncStream(failAt, unsupported);
            using (var engine = new LiteEngine(new EngineSettings
                { DataStream = data, LogStream = log, Password = EncryptedWalCreation_Tests.Password }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var rows = db.GetCollection("rows");
                Action commit = () => rows.Insert(new BsonDocument { ["_id"] = 999, ["value"] = "must not commit" });
                Record.Exception(commit).Should().BeSameAs(log.Failure);
                log.SyncCalls.Should().Be(failAt);
                data.ToArray().Should().Equal(original);
                var retainedWal = log.ToArray();
                Action nextWrite = () => rows.Insert(new BsonDocument { ["_id"] = 1000 });
                // The failed sync is recorded (decision 6 of docs/decisions/durability-policy.md), also one
                // that answers "cannot sync" (UnauthorizedAccessException): the engine continues read-only,
                // reads the old rows, and refuses writes with the record, whose inner exception is the
                // failure itself.
                rows.FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1, 2, 3, 4);
                rows.Find(Query.EQ("value", 1)).Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 3 });
                var record = ReadOnlyAfterWriteFailure.AssertReported(db, "A commit", null, log.Failure.Message, walKept: false);
                ReadOnlyAfterWriteFailure.AssertWriteRefused(nextWrite, record).InnerException.Should().BeSameAs(log.Failure);
                db.Rollback().Should().BeFalse("the failed commit left no transaction");
                rows.Count().Should().Be(4, "neither write is visible");
                data.ToArray().Should().Equal(original, "no write reaches the data file");
                log.ToArray().Should().Equal(retainedWal, "nothing is written behind the failed preamble");
            }

            // Process death keeps the failed barrier's cached bytes; power loss
            // keeps only earlier successful barriers. Both must preserve complete
            // old documents and indexes across read-only/retry/second open.
            EncryptedWalCreation_Tests.AssertRecovery(original, log.ToArray());
            EncryptedWalCreation_Tests.AssertRecovery(original, log.Durable);
        }

        /// <summary>
        /// With durable commits, the new encrypted WAL's preamble sync answers "cannot sync" (an
        /// UnauthorizedAccessException, which DiskService.IsDurableFlushUnsupported takes for #2242).
        /// The commit throws before it writes a frame, and its failure is recorded
        /// (LiteEngine.CommitAndReleaseTransaction records an UnauthorizedAccessException too), so the
        /// engine does not stay closed with even reads throwing "Dispose and reopen". Decisions 2, 3 and 6 (default A) of docs/decisions/durability-policy.md: the failure is
        /// recorded and reads keep working, as they do after the same preamble sync fails with an
        /// IOException (above) or a plain log file cannot sync
        /// (DurabilityPolicy_Tests.Commit_on_a_log_that_cannot_sync_throws_before_it_writes_a_frame).
        /// </summary>
        [Fact]
        public void FailedPreambleSyncThatCannotSync_KeepsReadsWorking()
        {
            var original = EncryptedWalCreation_Tests.LegacyData(legacy: false);
            using var data = ChecksumTestFiles.Copy(original);
            using var log = new FailedSyncStream(1, unsupported: true);
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = EncryptedWalCreation_Tests.Password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var rows = db.GetCollection("rows");
            Action commit = () => rows.Insert(new BsonDocument { ["_id"] = 999, ["value"] = "must not commit" });
            commit.Should().Throw<Exception>();

            rows.FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1, 2, 3, 4);
            var info = db.GetCollection("$database").FindAll().Single();
            info["readOnly"].AsBoolean.Should().BeTrue();
            info["writeFailure"]["operation"].AsString.Should().Be("A commit");
            Action nextWrite = () => rows.Insert(new BsonDocument { ["_id"] = 1000 });
            nextWrite.Should().Throw<IOException>().Which.Message.Should().StartWith(LiteEngine.WriteFailedPrefix);
            data.ToArray().Should().Equal(original);
        }

        private sealed class FailedSyncStream : MemoryStream, IDurableStream
        {
            private readonly int _failAt;
            internal readonly Exception Failure;
            internal byte[] Durable = Array.Empty<byte>();
            internal int SyncCalls;

            internal FailedSyncStream(int failAt, bool unsupported)
            {
                _failAt = failAt;
                Failure = unsupported ? (Exception)new UnauthorizedAccessException("preamble sync unsupported") :
                    new IOException("preamble sync failed");
            }

            public void FlushToDisk()
            {
                if (++SyncCalls >= _failAt) throw Failure;
                Durable = ToArray();
            }

            public override void Flush() { }
        }
    }
}
