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
    /// can write its backfill to a data file that never syncs it. If the WAL still syncs, emptying
    /// it became durable at the next log sync, before the backfill ever did: a power loss (each
    /// file as of its last successful sync) then lost every commit the WAL held, also commits
    /// acknowledged while durableLogFlush was true. A log sync now waits for a data sync that
    /// succeeds, so the WAL that last synced stays the durable one, and the WAL stays bounded.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    [Collection(NativeFileSyncCollection.Name)]
    public class UnsyncedBackfillPowerLoss_Tests
    {
        [Theory]
        [InlineData("shared")]
        [InlineData("second")] // the commit after the checkpoint comes from an independent connection
        [InlineData("direct")] // one long-lived engine, which also finds out when the data file syncs again
        public void Full_checkpoint_whose_data_sync_fails_keeps_every_durable_commit(string mode)
        {
            using var file = new TempFile();
            Setup(file.Filename);
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
            DurableLogFlush(firstDb).Should().BeFalse("the data file cannot sync");
            power.AfterPowerLoss(Rows).Should().Be(9, "the WAL that last synced holds every durable commit");

            using var second = mode == "second" ? new SharedEngine(power.Settings()) : null;
            using var writer = second == null ? firstDb : new LiteDatabase(second, disposeOnClose: false);
            Update(writer, 10);
            DurableLogFlush(writer).Should().BeFalse();
            power.AfterPowerLoss(Rows).Should().Be(9, "commit 10 is not durable: its log sync waits for the data file");

            power.DataFails = false;
            Update(writer, 11);
            power.AfterPowerLoss(Rows).Should().Be(11, "once the data file syncs again, the next log sync follows a data sync");
            writer.Checkpoint();
            new FileInfo(logName).Length.Should().Be(0, "a full checkpoint empties the WAL");
            power.AfterPowerLoss(Rows).Should().Be(11);
        }

        /// <summary>
        /// A full checkpoint on storage whose data file never synced for this engine empties the WAL
        /// as where neither file syncs: automatic checkpoints keep it near the checkpoint size (a
        /// kept WAL grew to 230 MB after 6,000 inserts of 2 KB). Only a WAL whose frames the engine
        /// synced is kept (DataFileStopsSyncing_Tests).
        /// </summary>
        [Fact]
        public void Wal_stays_bounded_where_only_the_data_file_cannot_sync()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            using (var power = new SyncPowerLossModel(file.Filename) { DataFails = true })
            using (var db = new LiteDatabase(new LiteEngine(power.Settings())))
            {
                db.CheckpointSize = 10;
                for (var id = 1; id <= 300; id++) db.GetCollection("log").Insert(new BsonDocument { ["_id"] = id, ["text"] = new string('t', 3000) });
                DurableLogFlush(db).Should().BeFalse();
                power.LogSyncs.Should().Be(0, "no log sync precedes a data sync that succeeds");
                new FileInfo(logName).Length.Should().BeLessThan(30L * Constants.PAGE_SIZE);
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

        /// <summary>Control: storage where neither file syncs for this engine still empties the WAL, as before.</summary>
        [Fact]
        public void Full_checkpoint_empties_the_wal_when_neither_file_syncs()
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
                new FileInfo(logName).Length.Should().Be(0);
            }
            using var reopened = new LiteDatabase(new LiteEngine(power.Settings()));
            reopened.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).Should().OnlyContain(x => x == 3);
        }

        /// <summary>
        /// Commits acknowledged durable (1..5), then neither file syncs during a full checkpoint: its
        /// backfill and the emptied WAL reach the OS cache only, which is all such storage offers.
        /// Then the WAL syncs again while the data file still does not. A later log sync (a commit,
        /// a checkpoint's journal, another connection's first sync, a reopen) made the emptied WAL
        /// durable while the backfill never was: a power loss lost commits 1..5. A log sync now
        /// waits for a data sync that succeeds, so the WAL they are in stays the durable one.
        /// </summary>
        [Theory]
        [InlineData("direct")] // one long-lived engine
        [InlineData("second")] // an independent connection syncs the WAL first
        [InlineData("reopen")] // the first engine is closed; a new one opens the files
        public void Commits_durable_before_neither_file_synced_survive_when_only_the_wal_syncs_again(string mode)
        {
            using var file = new TempFile();
            Setup(file.Filename);
            using (var setup = new LiteDatabase(file.Filename)) setup.GetCollection("rows").EnsureIndex("value");
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
                firstDb.Checkpoint();
                power.LogFails = false;
                if (mode == "reopen")
                {
                    firstDb.Dispose();
                    first.Dispose();
                }

                using var other = mode == "direct" ? null : new LiteEngine(power.Settings());
                var writer = other == null ? firstDb : new LiteDatabase(other, disposeOnClose: false);
                Update(writer, 6);
                DurableLogFlush(writer).Should().BeFalse("the data file cannot sync");
                writer.Checkpoint();
                Update(writer, 7);
                if (other != null) writer.Dispose();

                AssertAfterPowerLoss(power, 5);
            }
            finally
            {
                firstDb.Dispose();
                first.Dispose();
            }
        }

        /// <summary>
        /// A WAL an earlier engine synced (commit 1, no checkpoint since): an engine of a new process
        /// (which cannot know that) on storage where neither file syncs empties it and deletes it at close.
        /// A directory sync would make that deletion durable, and with it the loss of commit 1:
        /// while a log sync waits for the data file, so does the directory entry of the next WAL.
        /// </summary>
        [Fact]
        public void New_wal_directory_entry_waits_for_the_data_file_too()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new FilePowerLossModel(file.Filename);
            var directorySyncs = 0;
            NativeFileSync.SimulateDirectoryErrno = _ => { directorySyncs++; return 0; };
            try
            {
                using (var db = new LiteDatabase(file.Filename))
                {
                    db.CheckpointSize = 0;
                    Update(db, 1);
                    DurableLogFlush(db).Should().BeTrue();
                }
                power.DataFails = power.LogFails = true;
                DurableHeaders.Forget(file.Filename);
                using (var db = new LiteDatabase(file.Filename)) db.Checkpoint();
                File.Exists(logName).Should().BeFalse("closing the engine deleted its empty WAL");
                power.LogFails = false;
                directorySyncs = 0;
                using (var db = new LiteDatabase(file.Filename))
                {
                    Update(db, 2);
                    DurableLogFlush(db).Should().BeFalse();
                }
                directorySyncs.Should().Be(0, "the new WAL's name is not synced before the data file");
                power.AfterPowerLoss(db => db.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).Distinct().ToArray())
                    .Should().Equal(1);
            }
            finally
            {
                NativeFileSync.SimulateDirectoryErrno = null;
                File.Delete(logName);
            }
        }

        /// <summary>Every row holds <paramref name="value"/>, whole, and the index on value finds each one.</summary>
        private static void AssertAfterPowerLoss(SyncPowerLossModel power, int value)
        {
            power.AfterPowerLoss(Rows).Should().Be(value);
            using var image = new TempFile();
            var (data, log) = power.Capture();
            File.WriteAllBytes(image.Filename, data);
            File.WriteAllBytes(FileHelper.GetLogFile(image.Filename), log);
            try
            {
                using var db = new LiteDatabase(image.Filename);
                var rows = db.GetCollection("rows");
                rows.FindAll().OrderBy(x => x["_id"].AsInt32).Should().BeEquivalentTo(
                    Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)), o => o.WithStrictOrdering());
                rows.Find(Query.EQ("value", value)).Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(Enumerable.Range(1, Rows));
                rows.Count(Query.Not("value", value)).Should().Be(0);
            }
            finally { File.Delete(FileHelper.GetLogFile(image.Filename)); }
        }

        /// <summary>
        /// A 5.0.21 file with WAL commits (WalCrash_5_0_21.zip, see LegacyWalSharedMigration_Tests) on
        /// storage whose data file cannot sync while its WAL can. The conversion drains the legacy WAL
        /// and empties it; the converted header never syncs. Neither does the emptied WAL now, so a
        /// power loss leaves the legacy pair byte for byte, which opens with every commit. (The
        /// conversion was refused while a full checkpoint kept the WAL on such storage.)
        /// </summary>
        [Fact]
        public void Legacy_conversion_where_only_the_wal_syncs_leaves_the_legacy_pair_durable()
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
                    {
                        AssertLegacyCommits(db);
                        DurableLogFlush(db).Should().BeFalse();
                    }
                    image = power.Capture();
                }
                image.Data.Should().Equal(data);
                image.Log.Should().Equal(log);
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

        private static void Setup(string filename)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

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
