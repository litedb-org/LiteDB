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
    /// writes anything and writes nothing while it cannot, and the WAL is kept until a data sync
    /// that covers the backfill succeeds (decision 1 of docs/decisions/durability-policy.md), so the
    /// WAL stays beside the data file as last synced. Log syncs no longer wait for the data file
    /// (decision 4): while only the data file cannot sync, commits are durable in the kept WAL, as
    /// long as the data header they depend on is known to be on the device (implementation note 1),
    /// and a power loss keeps every commit acknowledged durable. Where that header is not known, a
    /// durable commit throws before it writes (decision 3).
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    [Collection(NativeFileSyncCollection.Name)]
    public class UnsyncedBackfillPowerLoss_Tests
    {
        /// <summary>
        /// Commits 1..9 acknowledged durable, then a full checkpoint finds the data file cannot sync:
        /// it writes nothing, so every durable commit is in the WAL beside the data file as last
        /// synced, and $database reports the kept WAL. Commits 10 and 11, made while the data file
        /// still cannot sync, sync the WAL and are reported durable in every mode (decision 4): by the
        /// engine or connection that saw the failure, and by an independent connection, whose fresh
        /// engine proves the data header by the one the last data sync left (DurableHeaders). A power
        /// loss keeps each of them, whole, with the value index; the checkpoint after each writes
        /// nothing. Once the data file syncs, a checkpoint empties the WAL and loses nothing.
        /// </summary>
        [Theory]
        [InlineData("shared")]
        [InlineData("second")] // the commits after the checkpoint come from an independent connection
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
            DurableLogFlush(firstDb).Should().BeTrue("commits stay durable in the WAL while only the data file cannot sync");
            WalKept(firstDb).Should().BeTrue();
            AssertAfterPowerLoss(power, 9);

            using var second = mode == "second" ? new SharedEngine(power.Settings()) : null;
            using var writer = second == null ? firstDb : new LiteDatabase(second, disposeOnClose: false);
            for (var value = 10; value <= 11; value++)
            {
                var logSyncs = power.LogSyncs;
                Update(writer, value);
                power.LogSyncs.Should().BeGreaterThan(logSyncs, "the commit synced the WAL");
                DurableLogFlush(writer).Should().BeTrue("the data header the WAL depends on is the one the last data sync left");
                AssertAfterPowerLoss(power, value); // a commit reported durable survives the power loss
                writer.Checkpoint();
                SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(power.Capture().Data, "no checkpoint writes to a data file that cannot sync");
                WalKept(writer).Should().BeTrue();
            }

            power.DataFails = false;
            Update(writer, 12);
            AssertAfterPowerLoss(power, 12);
            writer.Checkpoint();
            new FileInfo(logName).Length.Should().Be(0, "once the data file syncs, a full checkpoint empties the WAL");
            WalKept(writer).Should().BeFalse();
            AssertAfterPowerLoss(power, 12);
        }

        /// <summary>
        /// A full checkpoint on storage whose data file cannot sync cannot make its backfill durable,
        /// so the WAL is kept: it grows (a kept WAL grew to 230 MB after 6,000 inserts of 2 KB), and
        /// $database reports walKept, the log's size and the WAL limit. The database was created while
        /// the data file synced, so its header is on the device: every insert after the data file
        /// stopped syncing syncs the WAL and is durable there (decision 4), and a power loss keeps
        /// each, whole. Once the data file syncs, the next checkpoint empties the WAL.
        /// </summary>
        [Fact]
        public void Wal_is_kept_and_reported_while_the_data_file_cannot_sync()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            using (var power = new SyncPowerLossModel(file.Filename))
            {
                var pages = 0;
                var settings = power.Settings();
                settings.CheckpointStage = stage => { if (stage == "data-page") pages++; };
                using var db = new LiteDatabase(new LiteEngine(settings)); // created while the data file syncs
                db.CheckpointSize = 10;
                power.DataFails = true;
                var logSyncs = power.LogSyncs;
                for (var id = 1; id <= 300; id++) db.GetCollection("log").Insert(LogEntry(id));
                (power.LogSyncs - logSyncs).Should().BeGreaterOrEqualTo(300, "every commit synced the WAL");
                var info = Info(db);
                info["durableLogFlush"].AsBoolean.Should().BeTrue("commits stay durable in the WAL");
                info["walKept"].AsBoolean.Should().BeTrue();
                info["walLimit"].AsInt64.Should().Be(EngineSettings.DEFAULT_WAL_LIMIT);
                info["logFileSize"].AsInt64.Should().BeGreaterThan(150L * Constants.PAGE_SIZE, "every insert is still in the WAL");
                FilePowerLossModel.Open(power.Capture(), x => AssertLog(x, 300));
                pages = 0;
                db.Checkpoint();
                pages.Should().Be(0, "a checkpoint writes no page while the data file still cannot sync");

                power.DataFails = false;
                db.Checkpoint();
                WalKept(db).Should().BeFalse();
                new FileInfo(logName).Length.Should().Be(0, "once the data file syncs, a checkpoint empties the WAL");
                pages.Should().BeGreaterThan(0);
                FilePowerLossModel.Open(power.Capture(), x => AssertLog(x, 300));
            }
            using var reopened = new LiteDatabase(file.Filename);
            AssertLog(reopened, 300);
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
        /// syncing, so the WAL is kept as where only the data file cannot sync. With durable commits
        /// no commit can be made there (decision 3); commits opted out of them ("durable commits=false")
        /// reach the OS cache only and are reported so, without a recorded failure ("cannot sync" is
        /// the reason to opt out, proposed default A), and the strict WAL rule still applies to them
        /// (default D): the checkpoint writes nothing and keeps the WAL, so the data file as last
        /// synced with the WAL written back as it is holds every commit, whole, with the value index.
        /// </summary>
        [Fact]
        public void Full_checkpoint_keeps_the_wal_when_neither_file_syncs()
        {
            using var file = new TempFile();
            Setup(file.Filename, index: true);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename) { DataFails = true, LogFails = true };
            var settings = power.Settings();
            settings.DurableCommits = false;

            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                db.CheckpointSize = 0;
                for (var value = 1; value <= 3; value++) Update(db, value);
                db.Checkpoint();
                SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(power.Capture().Data, "the checkpoint wrote no page");
                var info = Info(db);
                info["walKept"].AsBoolean.Should().BeTrue();
                info["durableLogFlush"].AsBoolean.Should().BeFalse("opted-out commits reach the OS cache only");
                info["writeFailure"].IsNull.Should().BeTrue("\"cannot sync\" is not a failure without durable commits");
                new FileInfo(logName).Length.Should().BeGreaterThan(0);
                FilePowerLossModel.Open((power.Capture().Data, SyncPowerLossModel.ReadShared(logName)), x => AssertRows(x, 3));
            }
            new FileInfo(logName).Length.Should().BeGreaterThan(0, "closing kept the WAL too");
            FilePowerLossModel.Open((power.Capture().Data, SyncPowerLossModel.ReadShared(logName)), x => AssertRows(x, 3));
            using var reopened = new LiteDatabase(new LiteEngine(power.Settings()));
            AssertRows(reopened, 3);
        }

        /// <summary>
        /// Commits acknowledged durable (1..5), then neither file syncs during a full checkpoint, then
        /// the WAL syncs again while the data file still does not. The checkpoint's backfill and the
        /// emptied WAL reached the OS cache only, and a later log sync (a commit, a checkpoint's
        /// journal, another connection's first sync, a reopen) made the emptied WAL durable while the
        /// backfill never was: a power loss lost commits 1..5. The checkpoint now finds the data file
        /// cannot sync before it writes anything and changes neither file. Log syncs no longer wait
        /// for a data sync (decision 4): commits 6 and 7 of the engine that saw the failure, of an
        /// independent connection or of a reopen (which prove the untouched data file by its synced
        /// header) sync the WAL, are reported durable and survive the power loss, commit 7 also after
        /// a checkpoint of that engine that again wrote nothing.
        /// </summary>
        [Theory]
        [InlineData("direct")] // one long-lived engine
        [InlineData("second")] // an independent connection syncs the WAL first
        [InlineData("reopen")] // the first engine is closed; a new one opens the files
        public void Commits_durable_before_and_after_neither_file_synced_survive_when_only_the_wal_syncs_again(string mode)
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
                for (var value = 6; value <= 7; value++)
                {
                    Update(writer, value);
                    DurableLogFlush(writer).Should().BeTrue("the WAL syncs, and the data header it depends on is the one the last data sync left");
                    AssertAfterPowerLoss(power, value);
                    writer.Checkpoint();
                    SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(synced.Data, "no checkpoint writes to a data file that cannot sync");
                }
                WalKept(writer).Should().BeTrue();
                if (other != null) writer.Dispose();

                AssertAfterPowerLoss(power, 7);
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
        /// Nor can the new process know that the data header the WAL depends on is on the device
        /// (implementation note 1): its durable commit throws before it writes (decision 3), the
        /// failure is recorded (decision 6), reads keep working and the next write throws it.
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
                DurableLogs.Forget(Path.GetFullPath(logName));
                var wal = File.ReadAllBytes(logName);
                using (var db = new LiteDatabase(file.Filename))
                {
                    db.Checkpoint();
                    Action write = () => Update(db, 2);
                    write.Should().Throw<IOException>().WithMessage(HeaderNotProven + "*");
                    AssertRows(db, 1);
                    var failure = Info(db)["writeFailure"].AsDocument;
                    failure["file"].AsString.Should().Be("data");
                    failure["operation"].AsString.Should().Be("A commit");
                    failure["walKept"].AsBoolean.Should().BeTrue();
                    write.Should().Throw<IOException>().Which.Message.Should().StartWith(LiteEngine.WriteFailedPrefix + "A commit failed");
                    AssertRows(db, 1);
                }
                File.Exists(logName).Should().BeTrue("the WAL is kept");
                File.ReadAllBytes(logName).Should().Equal(wal, "the refused commit wrote no frame");
                File.ReadAllBytes(file.Filename).Should().Equal(power.Capture().Data, "the checkpoint wrote no page");

                var image = (power.Capture().Data, File.ReadAllBytes(logName));
                FilePowerLossModel.Open(image, db => AssertRows(db, 1));
            }
            finally { File.Delete(logName); }
        }

        /// <summary>The start of a durable commit's error where the data header the WAL depends on is not known to be on the device.</summary>
        internal const string HeaderNotProven = "This commit was not written: the data file cannot sync to the device (#2242) and its header is not known to be on the device";

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

        /// <summary>Document <paramref name="id"/> of the "log" collection: its payload differs per id.</summary>
        private static BsonDocument LogEntry(int id) => new BsonDocument { ["_id"] = id, ["text"] = new string((char)('a' + id % 26), 3000) + id };

        /// <summary>The "log" collection holds exactly <see cref="LogEntry"/> 1..<paramref name="count"/>, byte for byte.</summary>
        private static int AssertLog(LiteDatabase db, int count)
        {
            var log = db.GetCollection("log");
            var all = log.FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
            all.Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(1, count));
            for (var i = 0; i < count; i++) BsonSerializer.Serialize(all[i]).Should().Equal(BsonSerializer.Serialize(LogEntry(i + 1)));
            log.Count().Should().Be(count);
            return count;
        }

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();

        private static bool DurableLogFlush(LiteDatabase db) => Info(db)["durableLogFlush"].AsBoolean;

        private static bool WalKept(LiteDatabase db) => Info(db)["walKept"].AsBoolean;

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
