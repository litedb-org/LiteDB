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
    /// database worked. Data barriers now degrade like log barriers, for commits and checkpoints.
    /// Emptying a log still needs a data sync that succeeds (DiskService.KeepsWal): there a full
    /// checkpoint keeps the WAL, and a conversion or rebuild, which would empty one, is refused
    /// with both files unchanged (5.0.21 converted, unaware of what the log last synced); an open
    /// whose conversion is refused opens the file read-only instead. With durable commits (the
    /// default) a commit needs the data header the WAL depends on on the device (implementation
    /// note 1 of docs/decisions/durability-policy.md): on a database created where its data file
    /// cannot sync, the first commit throws before it writes (decision 3) and the engine continues
    /// read-only (decision 6). "durable commits=false" opts out and works as 5.0.21 did, with the WAL
    /// kept (proposed default D). Where the header is on the device, commits stay durable in the WAL
    /// while only the data file cannot sync (decision 4), and $database reports the kept WAL.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    [Collection(NativeFileSyncCollection.Name)]
    public class UnsyncableDataFile_Tests
    {
        /// <summary>
        /// Opted out of durable commits, a database on storage that cannot sync at all is created,
        /// indexed, written, checkpointed and reopened, as with 5.0.21. Its commits are reported
        /// non-durable, "cannot sync" is not recorded as a failure (proposed default A), and the
        /// checkpoints keep the WAL (default D); once the storage syncs, an engine with durable commits
        /// reports durability again and its checkpoint drains the WAL, every row and the index intact.
        /// </summary>
        [Theory]
        [InlineData(22)] // EINVAL: e.g. FUSE/virtual file systems without fsync support
        [InlineData(30)] // EROFS
        public void Database_on_storage_that_cannot_sync_is_created_checkpointed_and_reopened_without_durable_commits(int errno)
        {
            using var file = new TempFile();
            var optedOut = $"Filename={file.Filename};Durable Commits=false";

            NativeFileSync.SimulateErrno = _ => errno;
            try
            {
                using (var db = new LiteDatabase(optedOut))
                {
                    var rows = db.GetCollection("rows");
                    rows.EnsureIndex("value");
                    rows.Insert(Enumerable.Range(0, 100).Select(i => new BsonDocument { ["_id"] = i, ["value"] = i % 7 }));
                    db.Checkpoint();
                    rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = 100 });
                    var info = Info(db);
                    info["durableLogFlush"].AsBoolean.Should().BeFalse("storage that cannot sync must be reported, not hidden");
                    info["walKept"].AsBoolean.Should().BeTrue("no checkpoint moves the log into a data file that cannot sync");
                    info["writeFailure"].IsNull.Should().BeTrue("\"cannot sync\" is the reason to opt out, not a failure");
                }

                using (var db = new LiteDatabase(optedOut)) AssertRows(db);
            }
            finally
            {
                NativeFileSync.SimulateErrno = null;
            }

            using var synced = new LiteDatabase(file.Filename);
            AssertRows(synced);
            DurableLogFlush(synced).Should().BeTrue("a later engine on syncing storage reports durability again");
            synced.Checkpoint();
            new FileInfo(FileHelper.GetLogFile(file.Filename)).Length.Should().Be(0, "once the data file syncs, a checkpoint drains the WAL");
            AssertRows(synced);
        }

        /// <summary>
        /// A database created where its data file cannot sync: its header is not known to be on the
        /// device, and every WAL frame depends on it, so no log sync can make a commit durable
        /// (implementation note 1). With durable commits the first commit throws before it writes
        /// anything (decision 3), also where the log syncs, instead of being acknowledged: the data
        /// file is unchanged and no frame is written. The failure is recorded (decision 6): reads keep
        /// working, $database reports it without a write, and every later write throws it, also for a
        /// shared connection's later operations. It lasts until the database is reopened (proposed
        /// default C), where a commit is refused again while the header is still not on the device;
        /// once the storage syncs, a commit is durable.
        /// </summary>
        [Theory]
        [InlineData(22, false, false)] // EINVAL, nothing syncs
        [InlineData(30, false, false)] // EROFS
        [InlineData(22, true, false)]  // only the data file cannot sync
        [InlineData(22, true, true)]   // the same through a shared connection
        public void Durable_commit_to_a_database_created_where_its_data_file_cannot_sync_is_refused_before_it_writes(int errno, bool logSyncs, bool shared)
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            byte[] data;
            NativeFileSync.SimulateErrno = path => logSyncs && path.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase) ? 0 : errno;
            try
            {
                using (var db = new LiteDatabase(shared ? $"Filename={file.Filename};Connection=shared" : file.Filename))
                {
                    db.GetCollection("rows").Count().Should().Be(0);
                    data = File.ReadAllBytes(file.Filename);
                    Action insert = () => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "first" });
                    insert.Should().Throw<IOException>().WithMessage(UnsyncedBackfillPowerLoss_Tests.HeaderNotProven + "*");
                    File.ReadAllBytes(file.Filename).Should().Equal(data, "the refused commit wrote nothing");
                    LogLength(logName).Should().Be(0, "not a frame");

                    db.GetCollection("rows").Count().Should().Be(0, "reads keep working");
                    var info = Info(db);
                    info["readOnly"].AsBoolean.Should().BeTrue();
                    var failure = info["writeFailure"].AsDocument;
                    failure["file"].AsString.Should().Be("data");
                    failure["operation"].AsString.Should().Be("A commit");
                    failure["error"].AsString.Should().StartWith(UnsyncedBackfillPowerLoss_Tests.HeaderNotProven);
                    failure["walKept"].AsBoolean.Should().BeFalse();
                    Action index = () => db.GetCollection("rows").EnsureIndex("value");
                    index.Should().Throw<IOException>().Which.Message.Should().StartWith(LiteEngine.WriteFailedPrefix + "A commit failed");
                    insert.Should().Throw<IOException>().Which.Message.Should().StartWith(LiteEngine.WriteFailedPrefix + "A commit failed");
                    db.GetCollection("rows").FindAll().Should().BeEmpty();
                }
                File.ReadAllBytes(file.Filename).Should().Equal(data);

                using (var reopened = new LiteDatabase(file.Filename))
                {
                    Info(reopened)["writeFailure"].IsNull.Should().BeTrue("the failure lasts until the database is reopened");
                    Action insert = () => reopened.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "first" });
                    insert.Should().Throw<IOException>().WithMessage(UnsyncedBackfillPowerLoss_Tests.HeaderNotProven + "*");
                }
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                LogLength(logName).Should().Be(0);
            }
            finally { NativeFileSync.SimulateErrno = null; }

            using var synced = new LiteDatabase(file.Filename);
            synced.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "first" });
            DurableLogFlush(synced).Should().BeTrue();
            synced.GetCollection("rows").FindAll().Should().BeEquivalentTo(new[] { new BsonDocument { ["_id"] = 1, ["value"] = "first" } });
        }

        /// <summary>
        /// A data file that cannot sync while the log can, on a database whose header synced earlier
        /// in this process. Commits stay durable in the WAL (decision 4), so durableLogFlush stays
        /// true; what $database reports is the kept WAL (walKept, logFileSize and walLimit), and a
        /// power loss keeps every commit, also the checkpointed ones. Once the data file syncs, a
        /// checkpoint drains the WAL.
        /// </summary>
        [Fact]
        public void Data_file_that_cannot_sync_is_reported_even_when_the_log_can()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            using (var setup = new LiteDatabase(file.Filename)) setup.GetCollection("rows").EnsureIndex("value");
            using var power = new FilePowerLossModel(file.Filename) { DataFails = true };
            try
            {
                using var db = new LiteDatabase(file.Filename);
                db.GetCollection("rows").Insert(Enumerable.Range(1, 20).Select(Row));
                db.Checkpoint();
                var info = Info(db);
                info["durableLogFlush"].AsBoolean.Should().BeTrue("commits stay durable in the WAL");
                info["walKept"].AsBoolean.Should().BeTrue("checkpointed commits are not in the data file on the device");
                info["walLimit"].AsInt64.Should().Be(EngineSettings.DEFAULT_WAL_LIMIT);
                info["logFileSize"].AsInt64.Should().BeGreaterThan(0);
                info["writeFailure"].IsNull.Should().BeTrue();
                power.AfterPowerLoss(x => AssertRows(x, 20));

                power.DataFails = false;
                db.Checkpoint();
                Info(db)["walKept"].AsBoolean.Should().BeFalse();
                new FileInfo(logName).Length.Should().Be(0);
                power.AfterPowerLoss(x => AssertRows(x, 20));
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// A 5.0.21 file with WAL commits (WalCrash_5_0_21.zip) where nothing syncs: its conversion
        /// empties the legacy WAL, which needs a data sync, so a plain open threw "Cannot convert
        /// this legacy database now" and only an explicit "readonly=true;legacy index scan=true"
        /// open could read it. The plain open now falls back to that read-only open on its own:
        /// $database says why, every commit reads, a write throws naming the refusal and the
        /// instance keeps reading, and both files stay byte for byte. Once the storage syncs, a
        /// plain open converts.
        /// </summary>
        [Fact]
        public void Real_5_0_21_file_with_a_wal_opens_read_only_on_storage_that_cannot_sync()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            using (var zip = new ZipArchive(typeof(UnsyncableDataFile_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.WalCrash_5_0_21.zip"), ZipArchiveMode.Read))
            {
                using (var entry = zip.GetEntry("crash.db").Open())
                using (var output = File.Create(file.Filename)) entry.CopyTo(output);
                using (var entry = zip.GetEntry("crash-log.db").Open())
                using (var output = File.Create(logName)) entry.CopyTo(output);
            }
            var (data, log) = (File.ReadAllBytes(file.Filename), File.ReadAllBytes(logName));

            NativeFileSync.SimulateErrno = _ => 22;
            try
            {
                using (var db = new LiteDatabase(file.Filename))
                    UnsyncedReadOnlyOpen_Tests.AssertReadOnlyFallback(db, UnsyncedReadOnlyOpen_Tests.ConversionRefused, AssertLegacyCommits, "docs")
                        .Should().Contain("cannot sync");
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                File.ReadAllBytes(logName).Should().Equal(log);
                using var legacy = new LiteDatabase($"Filename={file.Filename};ReadOnly=true;Legacy Index Scan=true");
                AssertLegacyCommits(legacy);
            }
            finally { NativeFileSync.SimulateErrno = null; }

            using var converted = new LiteDatabase(file.Filename);
            converted.GetCollection("docs").Count(Query.EQ("value", 7)).Should().Be(21);
        }

        /// <summary>
        /// A rebuild on storage that cannot sync is refused before the engine closes and leaves no
        /// recovery marker that would block every open; the instance keeps reading. The rows are
        /// written opted out of durable commits: with them no commit could be made there at all.
        /// </summary>
        [Fact]
        public void Rebuild_on_storage_that_cannot_sync_is_refused_without_blocking_the_database()
        {
            using var file = new TempFile();
            var optedOut = $"Filename={file.Filename};Durable Commits=false";
            NativeFileSync.SimulateErrno = _ => 22;
            try
            {
                using (var db = new LiteDatabase(optedOut))
                {
                    db.GetCollection("rows").Insert(Enumerable.Range(0, 50).Select(i => new BsonDocument { ["_id"] = i }));
                    Action rebuild = () => db.Rebuild();
                    rebuild.Should().Throw<IOException>().WithMessage("Cannot rebuild this database now*");
                    db.GetCollection("rows").Count().Should().Be(50, "the refusal left the instance open");
                }
                using (var db = new LiteDatabase(optedOut)) db.GetCollection("rows").Count().Should().Be(50);
            }
            finally { NativeFileSync.SimulateErrno = null; }

            File.Exists(RebuildRecovery.GetMarkerFilename(file.Filename)).Should().BeFalse();
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(0, 50));
        }

        /// <summary>
        /// NativeFileSync synced read-only handles too, so the AES stream's preamble sync failed a
        /// read-only open of an encrypted database on storage that answers "cannot sync".
        /// FileStream.Flush(true), used by 5.0.21, skips handles that cannot write. A writable open
        /// still needs a successful preamble sync (documented) and fails without changing the file.
        /// </summary>
        [Fact]
        public void Encrypted_database_opens_read_only_on_storage_that_cannot_sync()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase($"Filename={file.Filename};Password=secret"))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "kept" });
            var original = File.ReadAllBytes(file.Filename);

            NativeFileSync.SimulateErrno = _ => 22;
            try
            {
                using (var db = new LiteDatabase($"Filename={file.Filename};Password=secret;ReadOnly=true"))
                    db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("kept");

                Action writable = () => new LiteDatabase($"Filename={file.Filename};Password=secret").Dispose();
                writable.Should().Throw<IOException>();
            }
            finally { NativeFileSync.SimulateErrno = null; }
            File.ReadAllBytes(file.Filename).Should().Equal(original);
        }

        /// <summary>
        /// A shared connection opens an engine per operation. After one found that the data file
        /// cannot sync, each later one finds it again: $database keeps reporting the kept WAL. The
        /// connection's commits stay durable in the WAL (decision 4; the header is the one a sync
        /// in this process left), so they are reported durable, which a power loss confirms.
        /// </summary>
        [Fact]
        public void Shared_connection_keeps_reporting_a_data_file_that_cannot_sync()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            using (var setup = new LiteDatabase(file.Filename)) setup.GetCollection("rows").EnsureIndex("value");
            using var power = new FilePowerLossModel(file.Filename) { DataFails = true };
            try
            {
                using var db = new LiteDatabase($"Filename={file.Filename};Connection=shared");
                db.GetCollection("rows").Insert(Row(1));
                db.Checkpoint();
                db.GetCollection("rows").Insert(Row(2));
                var info = Info(db);
                info["walKept"].AsBoolean.Should().BeTrue("a later operation's engine must keep reporting the kept WAL");
                info["durableLogFlush"].AsBoolean.Should().BeTrue("the commits are durable in the WAL");
                power.AfterPowerLoss(x => AssertRows(x, 2));
                AssertRows(db, 2);
            }
            finally { File.Delete(logName); }
        }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id % 3, ["payload"] = new string((char)('a' + id % 26), 500) + id };

        /// <summary>"rows" holds exactly <see cref="Row"/> 1..<paramref name="count"/>, and the value index finds each.</summary>
        private static int AssertRows(LiteDatabase db, int count)
        {
            var rows = db.GetCollection("rows");
            rows.FindAll().OrderBy(x => x["_id"].AsInt32).Should().BeEquivalentTo(Enumerable.Range(1, count).Select(Row), o => o.WithStrictOrdering());
            for (var value = 0; value < 3; value++)
                rows.Find(Query.EQ("value", value)).Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(Enumerable.Range(1, count).Where(id => id % 3 == value));
            return count;
        }

        /// <summary>The rows of the opted-out database test, after its update.</summary>
        private static void AssertRows(LiteDatabase db)
        {
            db.GetCollection("rows").Count().Should().Be(100);
            db.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(100);
            db.GetCollection("rows").Count(Query.EQ("value", 3)).Should().Be(14);
            db.GetCollection("rows").Find(Query.EQ("value", 100)).Select(x => x["_id"].AsInt32).Should().Equal(1);
        }

        private static long LogLength(string logName) => File.Exists(logName) ? new FileInfo(logName).Length : 0;

        private static void AssertLegacyCommits(LiteDatabase db)
        {
            var docs = db.GetCollection("docs").FindAll().ToList();
            docs.Should().HaveCount(101);
            docs.Count(x => x["value"].AsInt32 == 7).Should().Be(21);
        }

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();

        private static bool DurableLogFlush(LiteDatabase db) => Info(db)["durableLogFlush"].AsBoolean;
    }
}
#endif
