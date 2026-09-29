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
    /// #2242 in shared mode: every operation opens a fresh engine whose recovery registers
    /// the WAL slots an earlier engine cleared without a durable sync. The connection's later
    /// engines never reuse them, as they know the log cannot sync; a live reader keeps its
    /// snapshot, and the connection keeps reporting the weaker guarantee. Every retiring checkpoint
    /// first proves its syncs, so a log known unable to sync makes a checkpoint retire nothing. A
    /// log that stops syncing during the checkpoint keeps the retired frames, unless it stops only at
    /// the sync of the clears themselves. A second connection does not share the first one's
    /// diagnostic: opted out of durable commits it never syncs, and it reuses such cleared slots,
    /// whose witness root is durable (implementation note 15); a power loss keeps every commit synced.
    /// Opted out of durable commits "cannot sync" is not a failure (proposed default A of
    /// docs/decisions/durability-policy.md) and writes continue; with durable commits the
    /// checkpoint fails loudly and the connection continues read-only (decisions 3 and 6).
    /// Either way no checkpoint overwrites the data file behind a recovery copy the log cannot
    /// sync (decision D; external review, point 1): opted out, one whose log cannot sync before it
    /// writes returns quietly and leaves the data file byte for byte.
    /// </summary>
    public partial class SharedUnsyncableLog_Tests
    {
        private const int DocumentCount = 64; // Streams past the shared buffered-result budget.
        private const int LaterWrites = 5;

        /// <summary>
        /// Where the log cannot sync before the checkpoint writes ("retirement", "known"), the checkpoint
        /// returns quietly and the data file stays byte for byte. It used to backfill in write order
        /// behind a header journal in the OS cache only (external review, point 1; decision D).
        /// Fails today for "retirement" (engine defect): the log stops syncing after the retirement's
        /// proof, and the retirement's MVCC format promotion (DiskService.PrepareRetirement, before
        /// SyncLogBeforeCheckpoint, the one place that takes the refusal quietly) throws the refusal out
        /// of the checkpoint, whose stop records it as "A checkpoint" failure (DiskService.BeginCheckpointStop):
        /// db.Checkpoint() throws, and the connection continues read-only.
        /// </summary>
        [Theory]
        [InlineData("clear", false)]      // the log rejects only the sync of the checkpoint's clears
        [InlineData("clear", true)]       // same, and a second connection writes afterwards
        [InlineData("retirement", false)] // the log stops syncing after the proof: the checkpoint writes nothing
        [InlineData("retirement", true)]
        [InlineData("known", false)]      // the checkpoint's proof finds out: it retires and writes nothing
        public void Fresh_shared_engines_never_reuse_slots_on_a_log_their_connection_knows_cannot_sync_without_durable_commits(string stopsAt, bool secondConnection)
        {
            using var file = new TempFile();
            using var data = new SyncFile(file.Filename);
            var unsupported = new UnauthorizedAccessException("sync unsupported");
            using var log = new SyncFile(file.Filename + "-wal") { Failure = stopsAt == "known" ? unsupported : null };
            var armed = false;
            using var engine = new SharedEngine(new EngineSettings
            {
                Filename = file.Filename, DataStream = data, LogStream = log,
                CompactStorage = CompactStorageMode.Legacy, TransactionPageLimit = 1, DurableCommits = false,
                CheckpointStage = stage => { if (armed && stage == "wal-slot-cleared") log.Failure = unsupported; }
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            // No automatic or close checkpoints: every rejected sync below is a commit or a probe.
            db.CheckpointSize = 0;
            Write(db, "cold", 0);
            for (var value = 0; value <= 20; value++) Write(db, "docs", value);

            using var reader = engine.Query("docs", new Query());
            reader.Read().Should().BeTrue("the first read registers the reader's lease");
            reader.Current["value"].AsInt32.Should().Be(20);
            byte[] retired = null;
            var dataBefore = ReadAll(data);
            // Frames written before the checkpoint; the witness records it appends are discarded by
            // the next open when no root names them.
            var walBefore = ReadAll(log);
            var frames = walBefore.Length / WalChecksum.FrameSize;
            // Let the checkpoint's proof sync succeed; its retirement then meets the failure.
            if (stopsAt == "retirement")
            {
                log.AllowedSyncs = 1;
                log.Failure = unsupported;
            }
            armed = stopsAt == "clear";
            MvccCheckpoint_Tests.RunThread(() =>
            {
                db.Checkpoint();
                retired = ReadAll(log);
            });
            armed = false;
            log.Failure.Should().BeSameAs(unsupported);
            var cleared = BlankFrames(retired);
            if (stopsAt == "clear") cleared.Should().NotBeEmpty("the checkpoint reclaims versions the live reader does not need");
            else
            {
                cleared.Should().BeEmpty(stopsAt == "known"
                    ? "storage known unable to sync never retires frames"
                    : "a checkpoint whose log stopped syncing keeps the frames it would have retired");
                // Decision D: its retirement's format promotion and its backfill overwrite the data file
                // behind a header journal the log cannot sync; the checkpoint refuses both before either.
                ReadAll(data).Should().Equal(dataBefore, "a checkpoint whose log cannot sync writes nothing to the data file");
                retired.Take(frames * WalChecksum.FrameSize).Should().Equal(walBefore.Take(frames * WalChecksum.FrameSize), "the WAL is kept");
            }
            IsDurable(db).Should().BeFalse();

            var rejectedBefore = log.RejectedSyncs;
            using var second = secondConnection ? new SharedEngine(new EngineSettings
            {
                Filename = file.Filename, DataStream = data, LogStream = log,
                CompactStorage = CompactStorageMode.Legacy, TransactionPageLimit = 1, DurableCommits = false
            }) : null;
            using var writer = second == null ? db : new LiteDatabase(second, disposeOnClose: false);
            MvccCheckpoint_Tests.RunThread(() =>
            {
                for (var value = 1; value <= LaterWrites; value++) Write(writer, "cold", value);
            });

            var written = ReadAll(log);
            // Implementation note 15 of docs/decisions/durability-policy.md: reusing a slot needs no
            // sync of its own, as the root that witnesses it is durable (the checkpoint synced it before
            // its clears). The connection that found the log cannot sync reuses none; the second one
            // never learns it, as opted-out commits never sync, and reuses the cleared slots. (Before
            // that note an engine synced the log before its first reuse: the second connection asked
            // once, was refused, and appended.)
            if (stopsAt == "clear" && secondConnection)
                ChangedFrames(retired, written, frames).Should().BeGreaterThan(0, "the second connection reuses the witnessed slots");
            else ChangedFrames(retired, written, frames).Should().Be(0, "the connection knows the log cannot sync");
            (log.RejectedSyncs - rejectedBefore).Should().Be(0, "an opted-out commit never asks for a device sync, nor does a reused slot");
            IsDurable(db).Should().BeFalse("the connection keeps reporting the weaker guarantee across reopens");
            IsDurable(writer).Should().BeFalse();
            WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");
            WriteFailureAssert.NoneRecorded(writer);

            var count = 1;
            while (reader.Read())
            {
                reader.Current["value"].AsInt32.Should().Be(20, "the reader keeps its snapshot");
                count++;
            }
            count.Should().Be(DocumentCount);

            // A killed process leaves every byte in the OS cache; recover that image.
            using var dataCopy = new MemoryStream(ReadAll(data));
            using var logCopy = new MemoryStream(written);
            using var recovered = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = dataCopy, LogStream = logCopy, CompactStorage = CompactStorageMode.Legacy
            }));
            recovered.GetCollection("cold").FindAll().Should().HaveCount(DocumentCount)
                .And.OnlyContain(doc => doc["value"].AsInt32 == LaterWrites);
            recovered.GetCollection("docs").FindAll().Should().OnlyContain(doc => doc["value"].AsInt32 == 20);

            if (stopsAt != "clear") return;
            // A power loss keeps each file as of its last successful sync: the checkpoint synced both
            // files before its clears, the later opted-out commits never synced. Also with every reused
            // slot's new frame written back while the confirmations, which append, were not: the
            // witnessed slots hide the old frames and the unconfirmed new ones alike.
            var powerLoss = log.Durable;
            var reusedWrittenBack = (byte[])powerLoss.Clone();
            foreach (var offset in Enumerable.Range(0, frames).Select(frame => frame * WalChecksum.FrameSize))
            {
                if (offset + WalChecksum.FrameSize > powerLoss.Length ||
                    retired.Skip(offset).Take(WalChecksum.FrameSize).SequenceEqual(written.Skip(offset).Take(WalChecksum.FrameSize))) continue;
                Buffer.BlockCopy(written, offset, reusedWrittenBack, offset, WalChecksum.FrameSize);
            }
            reusedWrittenBack.SequenceEqual(powerLoss).Should().Be(!secondConnection, "only the second connection reused slots");
            foreach (var image in new[] { powerLoss, reusedWrittenBack })
            {
                using var dataImage = Expandable(data.Durable);
                using var logImage = Expandable(image);
                using var afterPowerLoss = new LiteDatabase(new LiteEngine(new EngineSettings
                {
                    DataStream = dataImage, LogStream = logImage, CompactStorage = CompactStorageMode.Legacy
                }));
                afterPowerLoss.GetCollection("cold").FindAll().Should().HaveCount(DocumentCount).And.OnlyContain(doc => doc["value"].AsInt32 == 0);
                afterPowerLoss.GetCollection("docs").FindAll().Should().HaveCount(DocumentCount).And.OnlyContain(doc => doc["value"].AsInt32 == 20);
            }
        }

        private static MemoryStream Expandable(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes, 0, bytes.Length);
            stream.Position = 0;
            return stream;
        }

        private static void Write(LiteDatabase db, string collection, int value)
        {
            db.GetCollection(collection).Upsert(Enumerable.Range(0, DocumentCount).Select(id =>
                new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1500) }));
        }

        private static bool IsDurable(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

        private static byte[] ReadAll(Stream stream)
        {
            lock (stream)
            {
                var position = stream.Position;
                var bytes = new byte[stream.Length];
                stream.Position = 0;
                var read = 0;
                while (read < bytes.Length) read += stream.Read(bytes, read, bytes.Length - read);
                stream.Position = position;
                return bytes;
            }
        }

        private static int[] BlankFrames(byte[] log) => Enumerable.Range(0, log.Length / WalChecksum.FrameSize)
            .Select(frame => frame * WalChecksum.FrameSize).Where(offset => IsBlank(log, offset)).ToArray();

        private static bool IsBlank(byte[] log, int offset) =>
            log.Skip(offset).Take(WalChecksum.FrameSize).All(value => value == 0);

        /// <summary>The first <paramref name="frames"/> frames of <paramref name="before"/> rewritten since (a reused slot).</summary>
        private static int ChangedFrames(byte[] before, byte[] after, int frames) => Enumerable.Range(0, frames)
            .Count(frame => !before.Skip(frame * WalChecksum.FrameSize).Take(WalChecksum.FrameSize)
                .SequenceEqual(after.Skip(frame * WalChecksum.FrameSize).Take(WalChecksum.FrameSize)));

        /// <summary>
        /// A file whose device syncs answer <see cref="Failure"/> once set, unless allowed.
        /// <see cref="Durable"/> is what a power loss keeps: the file as of its last successful sync.
        /// </summary>
        private sealed class SyncFile : FileStream
        {
            internal Exception Failure;
            internal int RejectedSyncs;
            internal int AllowedSyncs;
            internal byte[] Durable = new byte[0];

            internal SyncFile(string filename)
                : base(filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite,
                    4096, FileOptions.DeleteOnClose) { }

            public override void Flush(bool flushToDisk)
            {
                if (flushToDisk && Failure != null && AllowedSyncs > 0) AllowedSyncs--;
                else if (flushToDisk && Failure != null)
                {
                    base.Flush(false);
                    RejectedSyncs++;
                    throw Failure;
                }
                base.Flush(flushToDisk);
                if (flushToDisk) Durable = LiteDB.Tests.Regressions.SyncPowerLossModel.ReadShared(this.Name);
            }
        }
    }
}
