#if DEBUG || TESTING
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Since data barriers degrade on storage that answers "cannot sync" (#2242), a full checkpoint
    /// could write its backfill to a data file that never syncs it. If the WAL still syncs, emptying
    /// it became durable at the next log sync, before the backfill ever did: a power loss (each
    /// file as of its last successful sync) then lost every commit the WAL held, also commits
    /// acknowledged while durableLogFlush was true. A checkpoint now syncs the data file before it
    /// writes anything and writes nothing while it cannot, a log sync waits for a data sync that
    /// succeeds, and the WAL is kept until one does, so the WAL that last synced stays the durable
    /// one beside the data file as last synced.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    [Collection(NativeFileSyncCollection.Name)]
    public class UnsyncedBackfillPowerLoss_Tests
    {
        /// <summary>
        /// Commits 1..9 acknowledged durable, then a full checkpoint finds the data file cannot sync:
        /// it writes nothing, so every durable commit is in the WAL beside the data file as last
        /// synced. Commit 10 is reported durable only where a power loss keeps it: the engine or
        /// connection that saw the failure reports it non-durable (a direct engine's log sync waits
        /// for the data file); an independent connection's fresh engine finds the header the last
        /// data sync left (DurableHeaders), syncs the WAL and reports it durable, which it is, as the
        /// data file is exactly as last synced.
        /// </summary>
        [Theory]
        [InlineData("shared")]
        [InlineData("second")] // the commit after the checkpoint comes from an independent connection
        [InlineData("direct")] // one long-lived engine, which also finds out when the data file syncs again
        public void Full_checkpoint_whose_data_sync_fails_keeps_every_durable_commit(string mode)
        {
            using var file = new TempFile();
            Setup(file.Filename, index: true);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);

            using ILiteEngine first = mode == "direct" ? new LiteEngine(power.Settings()) : new SharedEngine(power.Settings());
            using var firstDb = new LiteDatabase(first, disposeOnClose: false);
            firstDb.CheckpointSize = 0;
            for (var value = 1; value <= 9; value++)
            {
                Update(firstDb, value);
                DurableLogFlush(firstDb).Should().BeTrue();
            }
            power.DataFails = true;
            firstDb.Checkpoint();
            SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(power.Capture().Data, "a checkpoint writes nothing to a data file that cannot sync");
            DurableLogFlush(firstDb).Should().BeFalse("the data file cannot sync");
            AssertAfterPowerLoss(power, 9);

            using var second = mode == "second" ? new SharedEngine(power.Settings()) : null;
            using var writer = second == null ? firstDb : new LiteDatabase(second, disposeOnClose: false);
            Update(writer, 10);
            var durable = DurableLogFlush(writer);
            durable.Should().Be(mode == "second", "only a connection that did not see the failure proves the data file by its synced header");
            if (durable) AssertAfterPowerLoss(power, 10);
            else if (mode == "direct") AssertAfterPowerLoss(power, 9); // its log sync waits for the data file
            else AssertAfterPowerLoss(power).Should().BeOneOf(9, 10);

            power.DataFails = false;
            Update(writer, 11);
            AssertAfterPowerLoss(power, 11); // once the data file syncs again, the next log sync follows a data sync
            writer.Checkpoint();
            new FileInfo(logName).Length.Should().Be(0, "a full checkpoint empties the WAL");
            AssertAfterPowerLoss(power, 11);
        }

        /// <summary>
        /// A full checkpoint on storage whose data file cannot sync cannot make its backfill durable,
        /// so the WAL is kept: it grows (a kept WAL grew to 230 MB after 6,000 inserts of 2 KB), and
        /// $database.walKept reports why. Once the data file syncs, the next checkpoint empties it.
        /// </summary>
        [Fact]
        public void Wal_is_kept_and_reported_while_the_data_file_cannot_sync()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            using (var power = new SyncPowerLossModel(file.Filename) { DataFails = true })
            {
                var pages = 0;
                var settings = power.Settings();
                settings.CheckpointStage = stage => { if (stage == "data-page") pages++; };
                using var db = new LiteDatabase(new LiteEngine(settings));
                db.CheckpointSize = 10;
                for (var id = 1; id <= 300; id++) db.GetCollection("log").Insert(new BsonDocument { ["_id"] = id, ["text"] = new string('t', 3000) });
                DurableLogFlush(db).Should().BeFalse();
                WalKept(db).Should().BeTrue();
                power.LogSyncs.Should().Be(0, "no log sync precedes a data sync that succeeds");
                new FileInfo(logName).Length.Should().BeGreaterThan(150L * Constants.PAGE_SIZE, "every insert is still in the WAL");
                pages = 0;
                db.Checkpoint();
                pages.Should().Be(0, "a checkpoint writes no page while the data file still cannot sync");

                power.DataFails = false;
                db.Checkpoint();
                WalKept(db).Should().BeFalse();
                new FileInfo(logName).Length.Should().Be(0, "once the data file syncs, a checkpoint empties the WAL");
                pages.Should().BeGreaterThan(0);
            }
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("log").Count().Should().Be(300);
        }

        /// <summary>
        /// A WAL the engine keeps in memory (LiteDatabase(Stream) without a log stream) stays
        /// bounded on a data stream that cannot sync.
        /// </summary>
        [Fact]
        public void In_memory_wal_is_still_emptied_on_a_data_stream_that_cannot_sync()
        {
            using var data = new UnsyncableStream();
            using var db = new LiteDatabase(data);
            db.CheckpointSize = 10;
            for (var id = 1; id <= 200; id++) db.GetCollection("log").Insert(new BsonDocument { ["_id"] = id, ["text"] = new string('t', 3000) });
            data.Rejected.Should().BeGreaterThan(0);
            db.GetCollection("$database").FindAll().Single()["logFileSize"].AsInt64.Should().BeLessThan(40 * Constants.PAGE_SIZE);
            db.GetCollection("log").Count().Should().Be(200);
        }

        /// <summary>
        /// Where neither file syncs, a full checkpoint emptied the WAL. This engine cannot know
        /// whether an earlier engine or process synced its frames before the storage stopped
        /// syncing, so the WAL is kept as where only the data file cannot sync.
        /// </summary>
        [Fact]
        public void Full_checkpoint_keeps_the_wal_when_neither_file_syncs()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename) { DataFails = true, LogFails = true };

            using (var db = new LiteDatabase(new LiteEngine(power.Settings())))
            {
                db.CheckpointSize = 0;
                for (var value = 1; value <= 3; value++) Update(db, value);
                db.Checkpoint();
                WalKept(db).Should().BeTrue();
                new FileInfo(logName).Length.Should().BeGreaterThan(0);
            }
            using var reopened = new LiteDatabase(new LiteEngine(power.Settings()));
            reopened.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).Should().OnlyContain(x => x == 3);
        }

        /// <summary>
        /// Commits acknowledged durable (1..5), then neither file syncs during a full checkpoint, then
        /// the WAL syncs again while the data file still does not. The checkpoint's backfill and the
        /// emptied WAL reached the OS cache only, and a later log sync (a commit, a checkpoint's
        /// journal, another connection's first sync, a reopen) made the emptied WAL durable while the
        /// backfill never was: a power loss lost commits 1..5. The checkpoint now finds the data file
        /// cannot sync before it writes anything and changes neither file, and a log sync waits for a
        /// data sync that succeeds. The engine that saw the failure reports commit 6 non-durable; a
        /// fresh engine (an independent connection, a reopen) proves the untouched data file by its
        /// synced header and reports it durable, and it survives. Commit 7, after that engine's own
        /// checkpoint found the data file cannot sync, is not durable.
        /// </summary>
        [Theory]
        [InlineData("direct")] // one long-lived engine
        [InlineData("second")] // an independent connection syncs the WAL first
        [InlineData("reopen")] // the first engine is closed; a new one opens the files
        public void Commits_durable_before_neither_file_synced_survive_when_only_the_wal_syncs_again(string mode)
        {
            using var file = new TempFile();
            Setup(file.Filename, index: true);
            using var power = new SyncPowerLossModel(file.Filename);

            var first = new LiteEngine(power.Settings());
            var firstDb = new LiteDatabase(first, disposeOnClose: false);
            try
            {
                firstDb.CheckpointSize = 0;
                for (var value = 1; value <= 5; value++)
                {
                    Update(firstDb, value);
                    DurableLogFlush(firstDb).Should().BeTrue();
                }
                power.DataFails = power.LogFails = true;
                var synced = power.Capture();
                var wal = SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(file.Filename));
                firstDb.Checkpoint();
                SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(synced.Data, "the checkpoint wrote no page");
                SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(file.Filename)).Should().Equal(wal, "nor changed the WAL");
                power.LogFails = false;
                if (mode == "reopen")
                {
                    firstDb.Dispose();
                    first.Dispose();
                }

                using var other = mode == "direct" ? null : new LiteEngine(power.Settings());
                var writer = other == null ? firstDb : new LiteDatabase(other, disposeOnClose: false);
                Update(writer, 6);
                var durable = DurableLogFlush(writer);
                durable.Should().Be(other != null, "only a fresh engine proves the untouched data file by its synced header");
                writer.Checkpoint();
                SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(synced.Data, "no checkpoint writes to a data file that cannot sync");
                Update(writer, 7);
                DurableLogFlush(writer).Should().BeFalse("the data file cannot sync");
                if (other != null) writer.Dispose();

                AssertAfterPowerLoss(power, durable ? 6 : 5);
            }
            finally
            {
                firstDb.Dispose();
                first.Dispose();
            }
        }

        /// <summary>
        /// A WAL an earlier engine synced (commit 1, no checkpoint since), then the storage stops
        /// syncing and the process restarts: the new process cannot know the WAL was synced. Its full
        /// checkpoint emptied the WAL (and its close deleted it); the OS could write that back ahead
        /// of the backfill, which never synced, and lose commit 1. Its checkpoint now finds the data
        /// file cannot sync before it writes anything: the data file stays as last synced and the WAL
        /// is kept, which is written back as it is. Every row, whole, and the value index survive.
        /// </summary>
        [Theory]
        [InlineData(true)]  // neither file syncs
        [InlineData(false)] // only the data file cannot sync
        public void Wal_an_earlier_process_synced_is_kept_after_a_restart(bool neither)
        {
            using var file = new TempFile();
            Setup(file.Filename, index: true);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new FilePowerLossModel(file.Filename);
            try
            {
                using (var db = new LiteDatabase(file.Filename))
                {
                    db.CheckpointSize = 0;
                    Update(db, 1);
                    DurableLogFlush(db).Should().BeTrue();
                }
                power.DataFails = true;
                power.LogFails = neither;
                DurableHeaders.Forget(file.Filename); // a new process
                using (var db = new LiteDatabase(file.Filename)) db.Checkpoint();
                File.Exists(logName).Should().BeTrue("the WAL is kept");
                File.ReadAllBytes(file.Filename).Should().Equal(power.Capture().Data, "the checkpoint wrote no page");

                var image = (power.Capture().Data, File.ReadAllBytes(logName));
                FilePowerLossModel.Open(image, db => AssertRows(db, 1));
            }
            finally { File.Delete(logName); }
        }

        private static int AssertAfterPowerLoss(SyncPowerLossModel power, int? value = null) => power.AssertAfterPowerLoss(Rows, value);

        private static int AssertRows(LiteDatabase db, int value) => SyncPowerLossModel.AssertRows(db, Rows, value);

        /// <summary>
        /// A 5.0.21 file with WAL commits (WalCrash_5_0_21.zip, see LegacyWalSharedMigration_Tests) on
        /// storage whose data file cannot sync while its WAL can. The conversion empties the legacy
        /// WAL once drained, which needs a data sync that succeeds: it is not run, and the open
        /// falls back to read-only ("Cannot convert this legacy database now"), which reads every
        /// commit and refuses writes; both files stay byte for byte, and a power loss leaves the
        /// legacy pair, which opens with every commit.
        /// </summary>
        [Fact]
        public void Legacy_conversion_opens_read_only_where_only_the_wal_syncs()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            var data = Entry("crash.db");
            var log = Entry("crash-log.db");
            File.WriteAllBytes(file.Filename, data);
            File.WriteAllBytes(logName, log);
            try
            {
                (byte[] Data, byte[] Log) image;
                using (var power = new SyncPowerLossModel(file.Filename) { DataFails = true })
                {
                    using (var db = new LiteDatabase(new LiteEngine(power.Settings())))
                        UnsyncedReadOnlyOpen_Tests.AssertReadOnlyFallback(db, UnsyncedReadOnlyOpen_Tests.ConversionRefused, AssertLegacyCommits, "docs")
                            .Should().Contain("cannot sync");
                    image = power.Capture();
                }
                image.Data.Should().Equal(data);
                image.Log.Should().Equal(log);
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                File.ReadAllBytes(logName).Should().Equal(log);
                FilePowerLossModel.Open(image, db => { AssertLegacyCommits(db); return 0; });
            }
            finally { File.Delete(logName); }
        }

        private static void AssertLegacyCommits(LiteDatabase db)
        {
            var docs = db.GetCollection("docs").FindAll().ToList();
            docs.Should().HaveCount(101);
            docs.Count(x => x["value"].AsInt32 == 7).Should().Be(21);
        }

        private sealed class UnsyncableStream : MemoryStream, IDurableStream
        {
            internal int Rejected;

            public void FlushToDisk()
            {
                Rejected++;
                throw new UnauthorizedAccessException("sync unsupported");
            }
        }

        private const int Rows = 64;

        private static void Setup(string filename, bool index = false)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            if (index) setup.GetCollection("rows").EnsureIndex("value");
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

        private static bool WalKept(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["walKept"].AsBoolean;

        private static byte[] Entry(string name)
        {
            using var resource = typeof(UnsyncedBackfillPowerLoss_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.WalCrash_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
#endif
