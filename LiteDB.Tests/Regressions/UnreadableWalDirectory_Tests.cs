#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Regression since 5.0.21: on Unix, the first durable commit of every engine and every
    /// checkpoint's header journal sync the WAL's directory with open(dir, O_RDONLY) + fsync
    /// (bef9aa3c3 #2998, 3b9e579f1 #3003). EACCES/EPERM from open() (a directory the process may
    /// write and search but not list, e.g. mode 0300/1733, or an AppArmor/SELinux profile granting
    /// file rw without directory read) failed the commit after its frames were written, stopped the
    /// engine and reported a commit that was actually persisted, and failed every checkpoint even
    /// with durable commits disabled. 5.0.21 never synced a directory.
    /// Now (decision 9 of docs/decisions/durability-policy.md) such a directory counts as one that
    /// cannot sync: a durable commit fails loudly before it writes anything, the engine keeps
    /// reading, and "durable commits=false" commits and checkpoints there as 5.0.21 did.
    /// The errno is injected (root bypasses directory permissions), so these run on every platform.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    [Collection(NativeFileSyncCollection.Name)]
    public class UnreadableWalDirectory_Tests
    {
        [Theory]
        [InlineData(13)] // EACCES
        [InlineData(1)]  // EPERM
        public void Commit_without_durable_commits_succeeds_when_the_wal_directory_cannot_be_opened_for_reading(int errno)
        {
            using var file = new TempFile();
            NativeFileSync.SimulateDirectoryErrno = _ => errno;
            try
            {
                using (var db = new LiteDatabase($"Filename={file.Filename};Durable Commits=false"))
                {
                    var rows = db.GetCollection("rows");
                    rows.Insert(new BsonDocument { ["_id"] = 1 });
                    rows.Insert(new BsonDocument { ["_id"] = 2 });
                    var info = db.GetCollection("$database").FindAll().Single();
                    info["durableLogFlush"].AsBoolean.Should().BeFalse("the new WAL's name is not claimed durable");
                    info["writeFailure"].IsNull.Should().BeTrue("the caller opted out: the directory is not asked");
                }
            }
            finally { NativeFileSync.SimulateDirectoryErrno = null; }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1, 2);
        }

        /// <summary>
        /// With durable commits (the default) the directory is proven before the engine's first WAL
        /// batch (decision 9): the commit throws before it writes a frame instead of reporting a
        /// persisted commit as failed. The files stay byte for byte, reads return exactly the earlier
        /// rows, $database reports the failure, and the next write throws with it (decision 6).
        /// </summary>
        [Theory]
        [InlineData(13)] // EACCES
        [InlineData(1)]  // EPERM
        public void Durable_commit_fails_before_it_writes_when_the_wal_directory_cannot_be_opened_for_reading(int errno)
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("rows").Insert(Enumerable.Range(0, 10).Select(i => new BsonDocument { ["_id"] = i }));
            }
            DurableLogs.Forget(Path.GetFullPath(logName)); // as in a new process: its directory is not known to sync
            var data = File.ReadAllBytes(file.Filename);
            var attempts = 0;
            NativeFileSync.SimulateDirectoryErrno = _ => { attempts++; return errno; };
            try
            {
                using (var db = new LiteDatabase(file.Filename))
                {
                    var rows = db.GetCollection("rows");
                    Action insert = () => rows.Insert(new BsonDocument { ["_id"] = 10 });
                    insert.Should().Throw<IOException>().WithMessage(WriteFailureAssert.DirectoryNotWritten + "*");
                    attempts.Should().Be(1);
                    TempFile.ReadAllBytesShared(file.Filename).Should().Equal(data);
                    LogLength(logName).Should().Be(0, "no frame was written");

                    rows.FindAll().Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(0, 10));
                    var reason = WriteFailureAssert.CommitRefused(db, WriteFailureAssert.DirectoryNotWritten);
                    WriteFailureAssert.Refused(() => rows.Insert(new BsonDocument { ["_id"] = 11 }), reason);
                    attempts.Should().Be(1, "a refused write asks the storage nothing");
                    TempFile.ReadAllBytesShared(file.Filename).Should().Equal(data);
                    LogLength(logName).Should().Be(0);
                }
            }
            finally { NativeFileSync.SimulateDirectoryErrno = null; }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(0, 10));
            reopened.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 10 });
            reopened.GetCollection("rows").Count().Should().Be(11);
        }

        [Fact]
        public void Checkpoint_without_durable_commits_succeeds_when_the_wal_directory_cannot_be_opened_for_reading()
        {
            using var file = new TempFile();
            NativeFileSync.SimulateDirectoryErrno = _ => 13; // EACCES
            try
            {
                using (var db = new LiteDatabase($"Filename={file.Filename};Durable Commits=false"))
                {
                    var rows = db.GetCollection("rows");
                    rows.Insert(Enumerable.Range(0, 50).Select(i => new BsonDocument { ["_id"] = i }));
                    db.Checkpoint();
                    rows.Insert(new BsonDocument { ["_id"] = 50 });
                }
            }
            finally { NativeFileSync.SimulateDirectoryErrno = null; }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").Count().Should().Be(51);
        }

        private static long LogLength(string logName) => File.Exists(logName) ? new FileInfo(logName).Length : 0;
    }
}
#endif
