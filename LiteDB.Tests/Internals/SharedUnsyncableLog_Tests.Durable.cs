using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public partial class SharedUnsyncableLog_Tests
    {
        /// <summary>
        /// With durable commits (the default) the retiring checkpoint whose log stops syncing throws
        /// (decision 3): its syncs are recovery barriers and the explicit checkpoint is the caller's own
        /// operation. Reads return exactly the committed values, and each later operation's engine, and
        /// a second connection's, relies on its own proof, which the log fails: its commit throws before
        /// it writes a frame. No slot is reused, and what a killed process leaves recovers exactly.
        /// (Keeping the record connection-wide, so that later writes throw it without asking the
        /// storage, is a later layer's.)
        /// </summary>
        [Theory]
        [InlineData("clear", false)]
        [InlineData("clear", true)]
        [InlineData("retirement", false)]
        [InlineData("retirement", true)]
        public void Shared_checkpoint_on_a_log_that_stops_syncing_fails_loudly_with_durable_commits(string stopsAt, bool secondConnection)
        {
            using var file = new TempFile();
            using var data = new SyncFile(file.Filename);
            var unsupported = new UnauthorizedAccessException("sync unsupported");
            using var log = new SyncFile(file.Filename + "-wal");
            var armed = false;
            using var engine = new SharedEngine(new EngineSettings
            {
                Filename = file.Filename, DataStream = data, LogStream = log,
                CompactStorage = CompactStorageMode.Legacy, TransactionPageLimit = 1,
                CheckpointStage = stage => { if (armed && stage == "wal-slot-cleared") log.Failure = unsupported; }
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            Write(db, "cold", 0);
            for (var value = 0; value <= 20; value++) Write(db, "docs", value);

            var frames = ReadAll(log).Length / WalChecksum.FrameSize;
            using (var reader = engine.Query("docs", new Query()))
            {
                reader.Read().Should().BeTrue("the first read registers the reader's lease");
                // Let the checkpoint's proof sync succeed; its retirement then meets the failure.
                if (stopsAt == "retirement")
                {
                    log.AllowedSyncs = 1;
                    log.Failure = unsupported;
                }
                armed = stopsAt == "clear";
                Action checkpoint = () => MvccCheckpoint_Tests.RunThread(() => db.Checkpoint());
                checkpoint.Should().Throw<IOException>().WithMessage(WriteFailureAssert.LogCannotSync + "*");
                armed = false;
            }
            log.Failure.Should().BeSameAs(unsupported);
            var retired = ReadAll(log);
            var cleared = BlankFrames(retired);
            if (stopsAt == "clear") cleared.Should().NotBeEmpty("the checkpoint reclaims versions the live reader does not need");
            else cleared.Should().BeEmpty("a checkpoint whose log stopped syncing keeps the frames it retired");

            AssertValues(db, "cold", 0);
            AssertValues(db, "docs", 20);
            IsDurable(db).Should().BeFalse();
            var rejectedBefore = log.RejectedSyncs;
            var dataBefore = ReadAll(data);

            if (!secondConnection)
            {
                // Each later operation opens a fresh engine, whose proof finds that the log cannot sync:
                // its commit fails before it writes (decision 3). Keeping the checkpoint's record for the
                // connection, so that a write is refused without asking the storage, is a later layer's.
                for (var value = 1; value <= LaterWrites; value++)
                {
                    Action later = () => Write(db, "cold", value);
                    later.Should().Throw<IOException>().WithMessage(WriteFailureAssert.LogNotWritten + "*");
                }
            }
            else
            {
                using var second = new SharedEngine(new EngineSettings
                {
                    Filename = file.Filename, DataStream = data, LogStream = log,
                    CompactStorage = CompactStorageMode.Legacy, TransactionPageLimit = 1
                });
                using var writer = new LiteDatabase(second, disposeOnClose: false);
                MvccCheckpoint_Tests.RunThread(() =>
                {
                    Action first = () => Write(writer, "cold", 1);
                    first.Should().Throw<IOException>().WithMessage(WriteFailureAssert.LogNotWritten + "*");
                });
                (log.RejectedSyncs - rejectedBefore).Should().Be(1, "only the second connection's proof asks the storage");
                AssertValues(writer, "cold", 0);
            }

            var written = ReadAll(log);
            written.Should().Equal(retired, "no frame was written, and no slot reused");
            ReadAll(data).Should().Equal(dataBefore);
            AssertValues(db, "cold", 0);

            // A killed process leaves every byte in the OS cache; recover that image.
            using var dataCopy = new MemoryStream(ReadAll(data));
            using var logCopy = new MemoryStream(written);
            using var recovered = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = dataCopy, LogStream = logCopy, CompactStorage = CompactStorageMode.Legacy
            }));
            AssertValues(recovered, "cold", 0);
            AssertValues(recovered, "docs", 20);
        }

        private static void AssertValues(LiteDatabase db, string collection, int value) =>
            db.GetCollection(collection).FindAll().Should().HaveCount(DocumentCount)
                .And.OnlyContain(doc => doc["value"].AsInt32 == value);
    }
}
