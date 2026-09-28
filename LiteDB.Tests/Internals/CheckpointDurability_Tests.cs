using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    /// <summary>
    /// Checkpoint and recovery-marker barriers on log storage whose sync fails or answers "cannot sync"
    /// (#2242), with and without durable commits (docs/decisions/durability-policy.md, decisions 3, 6
    /// and proposed default A). Power-loss images check exactly which state survives.
    /// </summary>
    public class CheckpointDurability_Tests
    {
        /// <summary>
        /// Marking the header invalid overwrites it in place, so it needs a durable header journal
        /// first: a failed journal sync stops it before any data write. Without durable commits the
        /// commit stays in the OS cache only; with them it synced, so a power loss keeps it either way.
        /// </summary>
        [Theory]
        [InlineData(null, false, false)]
        [InlineData("secret", false, false)]
        [InlineData(null, false, true)]
        [InlineData("secret", false, true)]
        [InlineData(null, true, false)]
        [InlineData("secret", true, false)]
        [InlineData(null, true, true)]
        [InlineData("secret", true, true)]
        public void RecoveryMarkerRequiresDurableJournalWhenSyncFails(string password, bool durableCommits, bool rejectJournalSync)
        {
            using var data = new CheckpointDevice();
            using var log = new CheckpointDevice();
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password, DurableCommits = durableCommits };
            using (var engine = new LiteEngine(settings))
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
                db.Commit().Should().BeTrue();
                log.RejectedSyncs.Should().Be(0);

                log.SuccessfulSyncsBeforeFailure = rejectJournalSync ? 1 : 0;
                log.SyncFailure = new IOException("durable sync failed");
                data.PersistFirstChangedWrite = true;
                var errors = engine.Close(LiteException.InvalidDatafileState("injected invalid page"));
                errors.Should().Contain(error => error is IOException && error.Message == "durable sync failed");
                data.ObservedWrites.Should().Be(0, "marking a header also requires durable recovery information");
                data.ToArray().Should().Equal(originalData);
                engine.GetMonitor().Transactions.Should().BeEmpty();
            }
            AssertRecovery(data.ToArray(), log.ToArray(), password, 1);
            AssertRecovery(data.Durable, log.Durable, password, durableCommits || rejectJournalSync ? 1 : 0);
        }

        /// <summary>
        /// Without durable commits a checkpoint on a log that answers "cannot sync" proceeds in write
        /// order (proposed default A): the log is marked degraded. A later checkpoint still retries a
        /// real sync, so storage that recovers regains its power-loss guarantee.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void CheckpointRetriesDurableSyncAfterAnEarlierCheckpointFellBack(string password)
        {
            using var data = new CheckpointDevice();
            using var log = new CheckpointDevice();
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password, DurableCommits = false }))
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
                db.Commit().Should().BeTrue();
                log.SuccessfulSyncsBeforeFailure = 0;
                engine.Checkpoint().Should().BeGreaterThan(0);
                log.RejectedSyncs.Should().BeGreaterThan(0, "the checkpoint tried a real sync");
                WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");

                rows.Update(Documents(2));
                log.SuccessfulSyncsBeforeFailure = -1;
                var durableLog = log.Durable;
                engine.Checkpoint().Should().BeGreaterThan(0);
                log.Durable.Should().NotBeSameAs(durableLog, "the checkpoint retried a real sync of the degraded log");
            }
            AssertRecovery(data.Durable, log.Durable, password, 2);
        }

        /// <summary>
        /// Commits that were acknowledged durable (or opted out) and a checkpoint whose log sync then
        /// fails, unlike an unsupported one, in both modes: the checkpoint throws before any data
        /// overwrite. The failure is sticky (decision 6 of docs/decisions/durability-policy.md): the
        /// engine keeps reading exactly the committed rows, $database reports it, and the next write
        /// throws with it (the engine closed for reads too before). A durable commit survives a power
        /// loss; an opted-out one only if the checkpoint's first barrier synced it.
        /// </summary>
        [Theory]
        [InlineData(null, false, false)]
        [InlineData("secret", false, false)]
        [InlineData(null, true, false)]
        [InlineData("secret", true, false)]
        [InlineData(null, false, true)]
        [InlineData("secret", false, true)]
        [InlineData(null, true, true)]
        [InlineData("secret", true, true)]
        public void FailedCheckpointSync_PreservesAtomicDataAndStopsWrites(
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
                db.Commit().Should().BeTrue();
                WriteFailureAssert.Info(db)["durableLogFlush"].AsBoolean.Should().Be(durableCommits);
                log.RejectedSyncs.Should().Be(0);
                var originalWal = log.ToArray();
                var preamble = password == null ? 0 : PAGE_SIZE;
                var frameBytes = (originalWal.Length - preamble) / WalChecksum.FrameSize * WalChecksum.FrameSize;

                // Exercise both barriers: the padded WAL and the sealed journal.
                // Unlike an unsupported sync, a failed one leaves redo durability unknown.
                log.SuccessfulSyncsBeforeFailure = rejectJournalSync ? 1 : 0;
                log.SyncFailure = new IOException("durable sync failed");
                data.PersistFirstChangedWrite = true;
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage("durable sync failed");
                data.ObservedWrites.Should().Be(0, "checkpoint needs durable redo before any data overwrite");
                data.ToArray().Should().Equal(originalData);
                log.ToArray().Take(preamble + (int)frameBytes).Should().Equal(originalWal.Take(preamble + (int)frameBytes));
                log.RejectedSyncs.Should().Be(1);

                rows.FindAll().Should().BeEquivalentTo(Documents(1));
                var reason = WriteFailureAssert.Recorded(db, "A checkpoint", "log", "durable sync failed", walKept: true);
                WriteFailureAssert.Refused(() => rows.Insert(new BsonDocument { ["_id"] = 100 }), reason);
                db.Rollback().Should().BeFalse("no transaction is open");
                data.ObservedWrites.Should().Be(0);
                log.RejectedSyncs.Should().Be(1, "a refused write asks the storage nothing");
                rows.FindAll().Should().BeEquivalentTo(Documents(1));
            }

            // Process death retains OS-cache bytes; they contain the whole commit.
            AssertRecovery(data.ToArray(), log.ToArray(), password, 1);
            // Power loss loses unsynced bytes. A durable commit, or a successful first barrier,
            // retains the update; otherwise only the fully checkpointed old state survives.
            AssertRecovery(data.Durable, log.Durable, password, durableCommits || rejectJournalSync ? 1 : 0);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void UnsupportedCheckpointSync_ProceedsInWriteOrderAndKeepsWriting_WithoutDurableCommits(string password)
        {
            // #2242: storage that cannot sync at all keeps working as before #2818 for callers that
            // opted out of durable commits ("cannot sync" is not a failure then, proposed default A).
            // Ordered OS-cache writes keep it consistent after a process crash;
            // power-loss safety is not claimed, so only the process image is checked.
            using var data = new CheckpointDevice();
            using var log = new CheckpointDevice();
            var settings = new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password, DurableCommits = false
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
                log.SuccessfulSyncsBeforeFailure = 0;

                db.BeginTrans();
                rows.Update(Documents(1));
                db.Commit().Should().BeTrue();
                engine.Checkpoint().Should().BeGreaterThan(0);
                log.RejectedSyncs.Should().BeGreaterThan(0, "checkpoint retries a real sync first");
                WriteFailureAssert.Info(db)["durableLogFlush"].AsBoolean.Should().BeFalse();

                rows.Update(Documents(2));
                db.Checkpoint();
                rows.Update(Documents(3));
                WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");
                AssertRecovery(data.ToArray(), log.ToArray(), password, 3);
            }
            AssertRecovery(data.ToArray(), log.ToArray(), password, 3);
        }

        /// <summary>
        /// With durable commits (the default) the same storage fails loudly (decision 3): the log was
        /// proven by earlier commits, so the commit whose own sync answers "cannot sync" has written its
        /// frames and throws with an unknown outcome (implementation note 4) instead of being
        /// acknowledged. No checkpoint follows; the engine keeps reading the files as they are, records
        /// the failure and refuses the next write (decision 6). A process crash keeps the commit (its
        /// frames reached the OS), a power loss keeps exactly the earlier state.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void UnsupportedCommitSync_FailsTheDurableCommitAndKeepsReading(string password)
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
                Action commit = () => db.Commit();
                commit.Should().Throw<IOException>().WithMessage(WriteFailureAssert.OutcomeUnknown + "*")
                    .Which.InnerException.Should().BeSameAs(log.SyncFailure);
                log.RejectedSyncs.Should().Be(1);
                data.ToArray().Should().Equal(originalData);

                rows.FindAll().Should().BeEquivalentTo(Documents(1), "its frames reached the operating system");
                var reason = WriteFailureAssert.Recorded(db, "A commit's log flush", "log", WriteFailureAssert.OutcomeUnknown, walKept: true);
                var wal = log.ToArray();
                WriteFailureAssert.Refused(() => rows.Update(Documents(2)), reason);
                log.RejectedSyncs.Should().Be(1, "a refused write asks the storage nothing");
                data.ToArray().Should().Equal(originalData);
                log.ToArray().Should().Equal(wal);
            }
            AssertRecovery(data.ToArray(), log.ToArray(), password, 1);
            AssertRecovery(data.Durable, log.Durable, password, 0);
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
            internal Exception SyncFailure = new UnauthorizedAccessException("durable sync unsupported");
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
                    throw SyncFailure;
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
