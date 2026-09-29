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
    /// default A of docs/decisions/durability-policy.md), but never reuses reclaimed WAL slots.
    /// Without a durable clear, a reused slot could overwrite a retired version that unsynced data
    /// pages still depend on after a power loss, so the WAL appends like dev. A retiring checkpoint
    /// first proves both files can sync, so a known rejection retires nothing; only a log that stops
    /// syncing during the retiring checkpoint, after that proof, leaves cleared slots behind.
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
        /// Every shared-mode operation opens a fresh engine. Its recovery registers the
        /// slots an earlier engine cleared without a durable sync, before any sync of its
        /// own has failed. It must not reuse them until one of its log syncs succeeds. The
        /// earlier engine opted out of durable commits, so its checkpoint left them behind; the
        /// fresh one commits durably on storage that syncs, and opts out on storage that cannot.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Fresh_engine_reuses_blank_slots_only_after_its_log_has_synced(bool syncable)
        {
            using var data = new MemoryStream();
            // Commits and the checkpoint's proof and retirement sync; the log then rejects the
            // sync that would make the checkpoint's slot clears durable.
            using var log = new UnsyncableLog { Syncable = true };
            byte[] crashedData, crashedLog;
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
            }
            BlankFrames(crashedLog).Should().NotBeEmpty("the checkpoint cleared the slots it retired");

            using var dataCopy = Copy(new MemoryStream(crashedData), new MemoryStream());
            using var logCopy = Copy(new MemoryStream(crashedLog), new UnsyncableLog { Syncable = syncable });
            using (var engine = Open(dataCopy, logCopy, durableCommits: syncable))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.Pragma(Pragmas.CHECKPOINT, 0);
                for (var value = 21; value <= 25; value++) Write(db, "cold", value);

                var written = logCopy.ToArray();
                var reused = BlankFrames(crashedLog).Count(offset => !IsBlank(written, offset));
                if (syncable)
                    reused.Should().BeGreaterThan(0, "after its first successful sync the engine reuses the blank slots");
                else
                    reused.Should().Be(0, "slots cleared without a durable sync must not be reused before a sync succeeds");
                db.GetCollection("cold").FindAll().Should().OnlyContain(doc => doc["value"].AsInt32 == 25);
            }

            AssertValues(dataCopy, logCopy, "cold", 25);
            AssertValues(dataCopy, logCopy, "docs", 20);
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

        /// <summary>A log whose storage answers every device sync with ERROR_ACCESS_DENIED.</summary>
        private sealed class UnsyncableLog : MemoryStream, IDurableStream
        {
            internal int Rejections;
            internal bool Syncable;
            internal int AllowedSyncs;

            public void FlushToDisk()
            {
                if (Syncable) return;
                if (AllowedSyncs > 0)
                {
                    AllowedSyncs--;
                    return;
                }
                Rejections++;
                throw new UnauthorizedAccessException("Access to the path is denied.");
            }
        }
    }
}
