using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class CheckpointDurability_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void RecoveryMarkerRequiresDurableJournalEvenAfterCommitFallback(string password, bool rejectJournalSync)
        {
            using var data = new CheckpointDevice();
            using var log = new CheckpointDevice();
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.Insert(Documents(0));
                rows.EnsureIndex("value");
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "unchanged" });
                db.Checkpoint();
                log.FlushToDisk();
                var originalData = data.ToArray();
                db.BeginTrans();
                rows.Update(Documents(1));
                log.SuccessfulSyncsBeforeFailure = 0;
                db.Commit().Should().BeTrue();
                log.RejectedSyncs.Should().Be(1);

                log.SuccessfulSyncsBeforeFailure = rejectJournalSync ? 1 : 0;
                data.PersistFirstChangedWrite = true;
                var errors = engine.Close(LiteException.InvalidDatafileState("injected invalid page"));
                errors.Should().Contain(error => error is UnauthorizedAccessException && error.Message == "durable sync unsupported");
                data.ObservedWrites.Should().Be(0, "marking a header also requires durable recovery information");
                data.ToArray().Should().Equal(originalData);
                engine.GetMonitor().Transactions.Should().BeEmpty();
            }
            AssertRecovery(data.ToArray(), log.ToArray(), password, 1);
            AssertRecovery(data.Durable, log.Durable, password, rejectJournalSync ? 1 : 0);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void CheckpointRetriesDurableSyncAfterCommitFallback(string password)
        {
            using var data = new CheckpointDevice();
            using var log = new CheckpointDevice();
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.Insert(Documents(0));
                rows.EnsureIndex("value");
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "unchanged" });
                db.Checkpoint();
                db.BeginTrans();
                rows.Update(Documents(1));
                log.SuccessfulSyncsBeforeFailure = 0;
                db.Commit().Should().BeTrue();
                log.RejectedSyncs.Should().Be(1);
                log.SuccessfulSyncsBeforeFailure = -1;
                engine.Checkpoint().Should().BeGreaterThan(0);
            }
            AssertRecovery(data.Durable, log.Durable, password, 1);
        }

        [Theory]
        [InlineData(null, false, false)]
        [InlineData("secret", false, false)]
        [InlineData(null, true, false)]
        [InlineData("secret", true, false)]
        [InlineData(null, false, true)]
        [InlineData("secret", false, true)]
        [InlineData(null, true, true)]
        [InlineData("secret", true, true)]
        public void UnsupportedCheckpointSync_PreservesAtomicDataAndStopsWrites(
            string password, bool durableCommits, bool rejectJournalSync)
        {
            using var data = new CheckpointDevice();
            using var log = new CheckpointDevice();
            var settings = new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password, DurableCommits = durableCommits
            };
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.Insert(Documents(0));
                rows.EnsureIndex("value");
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "unchanged" });
                db.Checkpoint();
                log.FlushToDisk(); // Establish the empty WAL as the durable pre-transaction state.
                var originalData = data.ToArray();

                db.BeginTrans();
                rows.Update(Documents(1));
                // With the default setting, exercise an already degraded commit.
                // With opt-out, the first rejected sync happens in checkpoint.
                log.SuccessfulSyncsBeforeFailure = 0;
                db.Commit().Should().BeTrue();
                db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean.Should().BeFalse();
                log.RejectedSyncs.Should().Be(durableCommits ? 1 : 0);
                var originalWal = log.ToArray();
                var preamble = password == null ? 0 : PAGE_SIZE;
                var frameBytes = (originalWal.Length - preamble) / WalChecksum.FrameSize * WalChecksum.FrameSize;

                // Exercise both barriers: the padded WAL and the sealed journal.
                log.SuccessfulSyncsBeforeFailure = rejectJournalSync ? 1 : 0;
                data.PersistFirstChangedWrite = true;
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<UnauthorizedAccessException>().WithMessage("durable sync unsupported");
                data.ObservedWrites.Should().Be(0, "checkpoint needs durable redo before any data overwrite");
                data.ToArray().Should().Equal(originalData);
                log.ToArray().Take(preamble + (int)frameBytes).Should().Equal(originalWal.Take(preamble + (int)frameBytes));
                log.RejectedSyncs.Should().Be(durableCommits ? 2 : 1, "checkpoint must retry real sync even after commit fallback");
                Action write = () => rows.Insert(new BsonDocument { ["_id"] = 100 });
                write.Should().Throw<LiteException>().WithMessage("*Dispose and reopen*");
                Action rollback = () => db.Rollback();
                rollback.Should().Throw<LiteException>().WithMessage("*Dispose and reopen*");
            }

            // Process death retains OS-cache bytes; they contain the whole commit.
            AssertRecovery(data.ToArray(), log.ToArray(), password, 1);
            // Power loss loses unsynced bytes. A successful first barrier retains
            // the update; otherwise only the fully checkpointed old state survives.
            AssertRecovery(data.Durable, log.Durable, password, rejectJournalSync ? 1 : 0);
        }

        private static void AssertRecovery(byte[] dataBytes, byte[] logBytes, string password, int value)
        {
            using var data = ChecksumTestFiles.Copy(dataBytes);
            using var log = ChecksumTestFiles.Copy(logBytes);
            foreach (var readOnly in new[] { true, false, true })
            {
                var beforeData = data.ToArray();
                var beforeLog = log.ToArray();
                using (var engine = new LiteEngine(new EngineSettings
                    { DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly }))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    var rows = db.GetCollection("rows");
                    rows.FindAll().Should().BeEquivalentTo(Documents(value));
                    rows.Find(Query.EQ("value", value)).Should().BeEquivalentTo(Documents(value));
                    rows.Find(Query.EQ("value", 1 - value)).Should().BeEmpty();
                    Assert.Equal(new BsonDocument { ["_id"] = 1, ["payload"] = "unchanged" },
                        db.GetCollection("cold").FindAll().Should().ContainSingle().Which);
                    if (!readOnly) db.Checkpoint();
                }
                if (readOnly)
                {
                    data.ToArray().Should().Equal(beforeData);
                    log.ToArray().Should().Equal(beforeLog);
                }
            }
        }

        private static BsonDocument[] Documents(int value) => Enumerable.Range(1, 16).Select(id => new BsonDocument
        {
            ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1500)
        }).ToArray();

        private sealed class CheckpointDevice : MemoryStream, IDurableStream
        {
            internal byte[] Durable = Array.Empty<byte>();
            internal int SuccessfulSyncsBeforeFailure = -1;
            internal int RejectedSyncs;
            internal int ObservedWrites;
            internal bool PersistFirstChangedWrite;
            private bool _powerLost;

            public void FlushToDisk()
            {
                if (_powerLost) throw new IOException("power loss");
                if (SuccessfulSyncsBeforeFailure == 0)
                {
                    RejectedSyncs++;
                    throw new UnauthorizedAccessException("durable sync unsupported");
                }
                if (SuccessfulSyncsBeforeFailure > 0) SuccessfulSyncsBeforeFailure--;
                Durable = ToArray();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_powerLost) throw new IOException("power loss");
                if (PersistFirstChangedWrite && count == PAGE_SIZE)
                {
                    ObservedWrites++;
                    var position = checked((int)Position);
                    var changed = position + count > Durable.Length ||
                        !buffer.Skip(offset).Take(count).SequenceEqual(Durable.Skip(position).Take(count));
                    if (changed)
                    {
                        // Model one complete data-page write reaching the device
                        // ahead of the unsynced WAL. All previously synced bytes
                        // outside this write remain intact.
                        if (Durable.Length < position + count) Array.Resize(ref Durable, position + count);
                        Buffer.BlockCopy(buffer, offset, Durable, position, count);
                        _powerLost = true;
                        throw new IOException("power loss after checkpoint data write");
                    }
                }
                base.Write(buffer, offset, count);
            }

            public override void Flush() { }
        }
    }
}
