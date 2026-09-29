using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    /// <summary>
    /// #2242 under MVCC: opted out of durable commits, a log that rejects every device sync keeps
    /// snapshot checkpoints and readers working ("cannot sync" is not a failure then, proposed
    /// default A of docs/decisions/durability-policy.md), but an engine that knows the log cannot
    /// sync never reuses reclaimed WAL slots: the WAL appends like dev. A retiring checkpoint first
    /// proves both files can sync, so a known rejection retires nothing; only a log that stops
    /// syncing during the retiring checkpoint, after that proof, leaves cleared slots behind, which a
    /// fresh engine that does not know reuses: their witness root is durable (implementation note 15).
    /// With durable commits that checkpoint fails loudly and the engine continues read-only
    /// (decisions 3, 6).
    /// </summary>
    public class MvccUnsyncableLog_Tests
    {
        private const int DocumentCount = 16;

        [Fact]
        public void Snapshot_checkpoint_on_a_log_that_never_syncs_appends_instead_of_reusing_slots_without_durable_commits()
        {
            using var data = new MemoryStream();
            using var log = new UnsyncableLog();
            using (var engine = Open(data, log, durableCommits: false))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.Pragma(Pragmas.CHECKPOINT, 0);
                Write(db, "cold", 0);
                Write(db, "docs", 0);
                for (var value = 1; value <= 20; value++) Write(db, "docs", value);

                using var reader = engine.Query("docs", new Query());
                var pinned = engine.GetWalIndex().SnapshotPositions(engine.ReadVersion);
                var before = log.ToArray();
                MvccCheckpoint_Tests.RunThread(() =>
                {
                    engine.Checkpoint();
                    var checkpointed = log.Length;
                    for (var value = 21; value <= 25; value++) Write(db, "cold", value);

                    // With slot reuse only the five confirmation frames would append.
                    ((log.Length - checkpointed) / WalChecksum.FrameSize).Should().BeGreaterThan(5,
                        "reclaimed slots must not be reused on storage that cannot sync");
                    var after = log.ToArray();
                    foreach (var position in pinned)
                    {
                        var offset = (int)(position / Constants.PAGE_SIZE * WalChecksum.FrameSize);
                        after.Skip(offset).Take(Constants.PAGE_SIZE).Should().Equal(before.Skip(offset).Take(Constants.PAGE_SIZE));
                    }
                });

                var count = 0;
                while (reader.Read())
                {
                    reader.Current["value"].AsInt32.Should().Be(20, "the reader keeps its snapshot");
                    count++;
                }
                count.Should().Be(DocumentCount);
                db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean.Should().BeFalse();
                log.Rejections.Should().BeGreaterThan(0);
                WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");
            }

            // A killed process leaves every byte in the OS cache; reopen that image.
            AssertValues(data, log, "cold", 25);
            AssertValues(data, log, "docs", 20);
        }

        /// <summary>
        /// Every shared-mode operation opens a fresh engine. Its recovery registers the slots an
        /// earlier engine cleared without a durable sync (that engine opted out of durable commits, so
        /// its checkpoint left them behind), before any sync of its own. The fresh engine commits
        /// durably on storage that syncs, and opts out on storage that cannot, where it never syncs.
        /// Either way it reuses those slots, without a sync of its own (implementation note 15 of
        /// docs/decisions/durability-policy.md): the checkpoint synced the root that witnesses them,
        /// so a clear that never reached the device leaves the old frame, which recovery skips. Until
        /// that note, an engine synced the log before its first reuse and appended where the sync was
        /// refused. What a killed process leaves, and what a power loss leaves (the log as of its last
        /// successful sync, also with the reused slots' new frames written back without their
        /// confirmations), recover exactly: every commit synced, none whose confirmation is missing.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Fresh_engine_reuses_witnessed_blank_slots_without_a_sync_of_its_own(bool syncable)
        {
            using var data = new MemoryStream();
            // Commits and the checkpoint's proof and retirement sync; the log then rejects the
            // sync that would make the checkpoint's slot clears durable.
            using var log = new UnsyncableLog { Syncable = true };
            byte[] crashedData, crashedLog, durableLog;
            using (var engine = Open(data, log, durableCommits: false))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.Pragma(Pragmas.CHECKPOINT, 0);
                Write(db, "cold", 0);
                Write(db, "docs", 0);
                for (var value = 1; value <= 20; value++) Write(db, "docs", value);
                using var reader = engine.Query("docs", new Query());
                engine.CheckpointStage = stage => { if (stage == "wal-slot-cleared") log.Syncable = false; };
                MvccCheckpoint_Tests.RunThread(() => engine.Checkpoint());
                log.Rejections.Should().Be(1, "only the sync of the clears was rejected");
                WriteFailureAssert.NoneRecorded(db);

                // A killed process: the cleared slots stay in the WAL.
                crashedData = data.ToArray();
                crashedLog = log.ToArray();
                durableLog = log.Durable;
            }
            BlankFrames(crashedLog).Should().NotBeEmpty("the checkpoint cleared the slots it retired");
            BlankFrames(durableLog).Should().BeEmpty("the clears never synced");

            using var dataCopy = Copy(new MemoryStream(crashedData), new MemoryStream());
            using var logCopy = Copy(new MemoryStream(crashedLog), new UnsyncableLog { Syncable = syncable, Durable = durableLog });
            byte[] written;
            using (var engine = Open(dataCopy, logCopy, durableCommits: syncable))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.Pragma(Pragmas.CHECKPOINT, 0);
                for (var value = 21; value <= 25; value++) Write(db, "cold", value);

                written = logCopy.ToArray();
                var reused = BlankFrames(crashedLog).Count(offset => !IsBlank(written, offset));
                // Implementation note 15: reusing a witnessed slot needs no sync, also where the engine
                // never syncs. (Before it, the refused sync of the log made that engine append: 0.)
                reused.Should().BeGreaterThan(0, "a fresh engine reuses the witnessed blank slots");
                logCopy.Rejections.Should().Be(0, "no sync was refused: the storage syncs, or the opted-out commits never ask");
                db.GetCollection("cold").FindAll().Should().OnlyContain(doc => doc["value"].AsInt32 == 25);
            }

            AssertValues(dataCopy, logCopy, "cold", 25);
            AssertValues(dataCopy, logCopy, "docs", 20);

            // A power loss: the log as of its last successful sync (commits 21 to 25 synced only where
            // the storage syncs), and the same with every reused slot's new frame written back while
            // the confirmations, which append, were not. The witnessed slots hide the old frames and
            // the unconfirmed new ones alike.
            var powerLoss = logCopy.Durable;
            var reusedWrittenBack = (byte[])powerLoss.Clone();
            foreach (var offset in BlankFrames(crashedLog).Where(offset => !IsBlank(written, offset) &&
                offset + WalChecksum.FrameSize <= reusedWrittenBack.Length))
                Buffer.BlockCopy(written, offset, reusedWrittenBack, offset, WalChecksum.FrameSize);
            reusedWrittenBack.SequenceEqual(powerLoss).Should().Be(syncable, "unsynced, the reused slots differ on the device");
            foreach (var image in new[] { powerLoss, reusedWrittenBack })
            {
                AssertValues(new MemoryStream(crashedData), new MemoryStream(image), "cold", syncable ? 25 : 0);
                AssertValues(new MemoryStream(crashedData), new MemoryStream(image), "docs", 20);
            }
        }

        /// <summary>
        /// With durable commits (the default) the snapshot checkpoint whose log stops syncing while it
        /// clears retired slots throws (decision 3): the clears' sync is a recovery barrier, and the
        /// explicit checkpoint is the caller's own operation. The failure is sticky (decision 6): reads
        /// return exactly the committed values, $database reports it, the next write throws with it,
        /// and the slots it cleared are never reused. What a killed process leaves recovers exactly.
        /// </summary>
        [Fact]
        public void Snapshot_checkpoint_whose_log_stops_syncing_while_it_clears_slots_fails_loudly_with_durable_commits()
        {
            using var data = new MemoryStream();
            using var log = new UnsyncableLog { Syncable = true };
            byte[] crashedData, crashedLog;
            using (var engine = Open(data, log))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.Pragma(Pragmas.CHECKPOINT, 0);
                Write(db, "cold", 0);
                Write(db, "docs", 0);
                for (var value = 1; value <= 20; value++) Write(db, "docs", value);
                using (engine.Query("docs", new Query()))
                {
                    engine.CheckpointStage = stage => { if (stage == "wal-slot-cleared") log.Syncable = false; };
                    Action checkpoint = () => MvccCheckpoint_Tests.RunThread(() => engine.Checkpoint());
                    checkpoint.Should().Throw<IOException>().WithMessage(WriteFailureAssert.LogCannotSync + "*");
                }
                log.Rejections.Should().Be(1, "only the sync of the clears was rejected");
                var cleared = log.ToArray();
                BlankFrames(cleared).Should().NotBeEmpty("the checkpoint cleared the slots it retired");

                db.GetCollection("docs").FindAll().Should().HaveCount(DocumentCount).And.OnlyContain(doc => doc["value"].AsInt32 == 20);
                db.GetCollection("cold").FindAll().Should().HaveCount(DocumentCount).And.OnlyContain(doc => doc["value"].AsInt32 == 0);
                var reason = WriteFailureAssert.Recorded(db, "A checkpoint", "log", WriteFailureAssert.LogCannotSync, walKept: true);
                crashedData = data.ToArray();
                WriteFailureAssert.Refused(() => Write(db, "cold", 21), reason);
                log.ToArray().Should().Equal(cleared, "a refused write reuses no cleared slot");
                data.ToArray().Should().Equal(crashedData);
                log.Rejections.Should().Be(1, "a refused write asks the storage nothing");
                crashedLog = log.ToArray();
            }

            // A killed process leaves every byte in the OS cache; reopen that image.
            AssertValues(new MemoryStream(crashedData), new MemoryStream(crashedLog), "cold", 0);
            AssertValues(new MemoryStream(crashedData), new MemoryStream(crashedLog), "docs", 20);
        }

        private static int[] BlankFrames(byte[] log) => Enumerable.Range(0, log.Length / WalChecksum.FrameSize)
            .Select(frame => frame * WalChecksum.FrameSize).Where(offset => IsBlank(log, offset)).ToArray();

        private static bool IsBlank(byte[] log, int offset) =>
            log.Skip(offset).Take(WalChecksum.FrameSize).All(value => value == 0);

        private static LiteEngine Open(Stream data, Stream log, bool durableCommits = true) => new LiteEngine(new EngineSettings
        {
            CompactStorage = CompactStorageMode.Legacy, DataStream = data, LogStream = log, TransactionPageLimit = 1,
            DurableCommits = durableCommits
        });

        private static void Write(LiteDatabase db, string collection, int value)
        {
            db.GetCollection(collection).Upsert(Enumerable.Range(0, DocumentCount).Select(id =>
                new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1500) }));
        }

        // Reopen on the same storage (it cannot sync), so without durable commits.
        private static void AssertValues(MemoryStream data, MemoryStream log, string collection, int value)
        {
            using var dataCopy = Copy(data, new MemoryStream());
            using var logCopy = Copy(log, new UnsyncableLog());
            using (var engine = Open(dataCopy, logCopy, durableCommits: false))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection(collection).FindAll().Should().HaveCount(DocumentCount)
                    .And.OnlyContain(doc => doc["value"].AsInt32 == value);
                db.Checkpoint();
            }
            using var reopenedEngine = Open(dataCopy, logCopy, durableCommits: false);
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection(collection).FindAll().Should().OnlyContain(doc => doc["value"].AsInt32 == value);
        }

        private static T Copy<T>(MemoryStream source, T target) where T : MemoryStream
        {
            var bytes = source.ToArray();
            target.Write(bytes, 0, bytes.Length);
            target.Position = 0;
            return target;
        }

        /// <summary>
        /// A log whose storage answers every device sync with ERROR_ACCESS_DENIED, unless
        /// <see cref="Syncable"/> or allowed. <see cref="Durable"/> is what a power loss keeps: the
        /// log as of its last successful sync.
        /// </summary>
        private sealed class UnsyncableLog : MemoryStream, IDurableStream
        {
            internal int Rejections;
            internal bool Syncable;
            internal int AllowedSyncs;
            internal byte[] Durable = new byte[0];

            public void FlushToDisk()
            {
                if (Syncable || AllowedSyncs > 0)
                {
                    if (!Syncable) AllowedSyncs--;
                    Durable = ToArray();
                    return;
                }
                Rejections++;
                throw new UnauthorizedAccessException("Access to the path is denied.");
            }
        }
    }
}
