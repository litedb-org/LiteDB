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
    /// with both files unchanged (5.0.21 converted, unaware of what the log last synced).
    /// "durable commits=false" opts out and works as 5.0.21 did, with the WAL kept (proposed default
    /// D). Where only the data file cannot sync, commits stay durable in the WAL (decision 4) and
    /// $database reports the kept WAL.
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

        /// <summary>A durable commit refused before it wrote, because a file cannot sync (#2242).</summary>
        internal const string CommitNotWritten = "This commit was not written: *cannot sync to the device (#2242)*";

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

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();

        private static bool DurableLogFlush(LiteDatabase db) => Info(db)["durableLogFlush"].AsBoolean;
    }
}
#endif
