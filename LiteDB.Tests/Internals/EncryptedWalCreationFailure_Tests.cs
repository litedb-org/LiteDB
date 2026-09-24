using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
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
                nextWrite.Should().Throw<Exception>().WithMessage("*Dispose and reopen*");
                Action rollback = () => db.Rollback();
                rollback.Should().Throw<Exception>().WithMessage("*Dispose and reopen*");
                log.ToArray().Should().Equal(retainedWal);
            }

            // Process death keeps the failed barrier's cached bytes; power loss
            // keeps only earlier successful barriers. Both must preserve complete
            // old documents and indexes across read-only/retry/second open.
            EncryptedWalCreation_Tests.AssertRecovery(original, log.ToArray());
            EncryptedWalCreation_Tests.AssertRecovery(original, log.Durable);
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
