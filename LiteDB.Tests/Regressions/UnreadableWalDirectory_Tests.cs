#if DEBUG || TESTING
using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Regression since 5.0.21: on Unix, the first durable commit of every engine and every
    /// checkpoint's header journal sync the WAL's directory with open(dir, O_RDONLY) + fsync
    /// (bef9aa3c3 #2998, 3b9e579f1 #3003). Only EINVAL/EROFS/ENOTSUP are treated as "cannot sync";
    /// EACCES/EPERM from open() (a directory the process may write and search but not list, e.g.
    /// mode 0300/1733, or an AppArmor/SELinux profile granting file rw without directory read)
    /// fails the commit, stops the engine and reports a commit that was actually persisted, and
    /// fails every checkpoint even with durable commits disabled. 5.0.21 never synced a directory.
    /// The errno is injected (root bypasses directory permissions), so these run on every platform.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    [Collection(NativeFileSyncCollection.Name)]
    public class UnreadableWalDirectory_Tests
    {
        [Theory]
        [InlineData(13)] // EACCES
        [InlineData(1)]  // EPERM
        public void Commit_succeeds_when_the_wal_directory_cannot_be_opened_for_reading(int errno)
        {
            using var file = new TempFile();
            NativeFileSync.SimulateDirectoryErrno = _ => errno;
            try
            {
                using (var db = new LiteDatabase(file.Filename))
                {
                    var rows = db.GetCollection("rows");
                    rows.Insert(new BsonDocument { ["_id"] = 1 });
                    rows.Insert(new BsonDocument { ["_id"] = 2 });
                    db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean
                        .Should().BeFalse("the new WAL's name is not claimed durable");
                }
            }
            finally { NativeFileSync.SimulateDirectoryErrno = null; }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").Count().Should().Be(2);
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
    }
}
#endif
