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
    /// Regression since 5.0.21: storage that answers "cannot sync" (EINVAL/EROFS/ENOTSUP, #2242)
    /// only degraded for the WAL. Every data-file sync (Initialize, checkpoint WriteDataDisk,
    /// EnableChecksums, salt rotation, header journal, promotion) went through NativeFileSync and
    /// threw FileSyncException, so a database on such a mount could no longer be created,
    /// converted or checkpointed, and a rebuild left a recovery marker that blocked every open.
    /// 5.0.21 used FileStream.Flush(true), which on Unix ignores exactly these errnos, so the same
    /// database worked. The release notes promise the earlier behaviour "for commits, checkpoints
    /// and format conversion" on such storage: data barriers now degrade like log barriers.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    [Collection(NativeFileSyncCollection.Name)]
    public class UnsyncableDataFile_Tests
    {
        [Theory]
        [InlineData(22)] // EINVAL: e.g. FUSE/virtual file systems without fsync support
        [InlineData(30)] // EROFS
        public void Database_on_storage_that_cannot_sync_is_created_checkpointed_and_reopened(int errno)
        {
            using var file = new TempFile();

            NativeFileSync.SimulateErrno = _ => errno;
            try
            {
                using (var db = new LiteDatabase(file.Filename))
                {
                    var rows = db.GetCollection("rows");
                    rows.EnsureIndex("value");
                    rows.Insert(Enumerable.Range(0, 100).Select(i => new BsonDocument { ["_id"] = i, ["value"] = i % 7 }));
                    db.Checkpoint();
                    rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = 100 });
                    DurableLogFlush(db).Should().BeFalse("storage that cannot sync must be reported, not hidden");
                }

                using (var db = new LiteDatabase(file.Filename))
                {
                    db.GetCollection("rows").Count().Should().Be(100);
                    db.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(100);
                    db.GetCollection("rows").Count(Query.EQ("value", 3)).Should().Be(14);
                }
            }
            finally
            {
                NativeFileSync.SimulateErrno = null;
            }

            using var synced = new LiteDatabase(file.Filename);
            synced.GetCollection("rows").Count().Should().Be(100);
            DurableLogFlush(synced).Should().BeTrue("a later engine on syncing storage reports durability again");
        }

        [Fact]
        public void Data_file_that_cannot_sync_is_reported_even_when_the_log_can()
        {
            using var file = new TempFile();
            NativeFileSync.SimulateErrno = path => path.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase) ? 0 : 22;
            try
            {
                using var db = new LiteDatabase(file.Filename);
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                db.Checkpoint();
                DurableLogFlush(db).Should().BeFalse("checkpointed commits are not power-loss safe");
            }
            finally { NativeFileSync.SimulateErrno = null; }
        }

        [Fact]
        public void Real_5_0_21_file_with_a_wal_converts_on_storage_that_cannot_sync()
        {
            using var file = new TempFile();
            using (var zip = new ZipArchive(typeof(UnsyncableDataFile_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.WalCrash_5_0_21.zip"), ZipArchiveMode.Read))
            {
                using (var entry = zip.GetEntry("crash.db").Open())
                using (var output = File.Create(file.Filename)) entry.CopyTo(output);
                using (var entry = zip.GetEntry("crash-log.db").Open())
                using (var output = File.Create(FileHelper.GetLogFile(file.Filename))) entry.CopyTo(output);
            }

            NativeFileSync.SimulateErrno = _ => 22;
            try
            {
                using var db = new LiteDatabase(file.Filename);
                var docs = db.GetCollection("docs").FindAll().ToList();
                docs.Should().HaveCount(101);
                docs.Count(x => x["value"].AsInt32 == 7).Should().Be(21);
            }
            finally { NativeFileSync.SimulateErrno = null; }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("docs").Count(Query.EQ("value", 7)).Should().Be(21);
        }

        [Fact]
        public void Rebuild_on_storage_that_cannot_sync_completes_without_blocking_the_database()
        {
            using var file = new TempFile();
            NativeFileSync.SimulateErrno = _ => 22;
            try
            {
                using (var db = new LiteDatabase(file.Filename))
                {
                    db.GetCollection("rows").Insert(Enumerable.Range(0, 50).Select(i => new BsonDocument { ["_id"] = i }));
                    db.Rebuild();
                    db.GetCollection("rows").Count().Should().Be(50);
                }
            }
            finally { NativeFileSync.SimulateErrno = null; }

            File.Exists(RebuildRecovery.GetMarkerFilename(file.Filename)).Should().BeFalse();
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").Count().Should().Be(50);
        }

        /// <summary>
        /// Retirement witnesses need a durable sync and a reused WAL slot a durable clear. Once the
        /// data file answered "cannot sync", checkpoints under a live reader neither retire frames
        /// (no v13 promotion, no witness) nor let later commits reuse slots. Control: storage that
        /// syncs retires the superseded frames and reuses their slots.
        /// </summary>
        [Theory]
        [InlineData("syncs")]
        [InlineData("detected-earlier")]      // by a checkpoint before the reader
        [InlineData("detected-by-partial")]   // by the partial checkpoint itself
        public void Storage_that_cannot_sync_neither_retires_nor_reuses_wal_frames(string mode)
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
            {
                setup.GetCollection("rows").Insert(Enumerable.Range(1, 8).Select(id => MvccRetirementScenario.Document(id, 0)));
            }
            var version = Header(file.Filename)[HeaderPage.P_FILE_VERSION];
            version.Should().BeLessThan(HeaderPage.MVCC_FILE_VERSION);

            if (mode != "syncs") NativeFileSync.SimulateErrno = path => path.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase) ? 0 : 22;
            try
            {
                using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                if (mode == "detected-earlier")
                {
                    Update(db, 1);
                    db.Checkpoint();
                }
                for (var value = 2; value <= 5; value++) Update(db, value);

                using (var reader = engine.Query("rows", new Query()))
                {
                    Worker(() =>
                    {
                        for (var value = 6; value <= 8; value++) Update(db, value);
                        var start = LogFileSize(db);
                        Update(db, 9);
                        var perUpdate = LogFileSize(db) - start;
                        perUpdate.Should().BeGreaterThan(0);

                        engine.Checkpoint();
                        var before = LogFileSize(db);
                        for (var value = 10; value <= 13; value++) Update(db, value);
                        var growth = LogFileSize(db) - before;
                        if (mode == "syncs") growth.Should().BeLessThan(4 * perUpdate, "syncing storage reuses retired slots");
                        else growth.Should().Be(4 * perUpdate, "commits after a checkpoint that could not sync append");
                    });
                }

                var promoted = Header(file.Filename)[HeaderPage.P_FILE_VERSION] == HeaderPage.MVCC_FILE_VERSION;
                if (mode == "syncs") promoted.Should().BeTrue("the control retires frames");
                if (mode == "detected-earlier") promoted.Should().BeFalse("no retirement witness is published without a durable sync");
            }
            finally { NativeFileSync.SimulateErrno = null; }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).Should().OnlyContain(x => x == 13);
        }

        [Fact]
        public void Shared_connection_keeps_reporting_a_data_file_that_cannot_sync()
        {
            using var file = new TempFile();
            NativeFileSync.SimulateErrno = path => path.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase) ? 0 : 22;
            try
            {
                using var db = new LiteDatabase($"Filename={file.Filename};Connection=shared");
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                db.Checkpoint();
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                DurableLogFlush(db).Should().BeFalse("a later operation's engine must not claim durability again");
            }
            finally { NativeFileSync.SimulateErrno = null; }
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Update(Enumerable.Range(1, 8).Select(id => MvccRetirementScenario.Document(id, value))).Should().Be(8);

        private static long LogFileSize(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["logFileSize"].AsInt64;

        private static void Worker(Action action)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            });
            thread.Start();
            thread.Join();
            failure?.Throw();
        }

        private static byte[] Header(string filename)
        {
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[Constants.PAGE_SIZE];
            stream.ReadFully(header, 0, header.Length);
            return header;
        }

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;
    }
}
#endif
