#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Regression since 5.0.21: storage that answers "cannot sync" (EINVAL/EROFS/ENOTSUP, #2242)
    /// only degrades for the WAL. Every data-file sync (Initialize, checkpoint WriteDataDisk,
    /// EnableChecksums, header journal) goes through NativeFileSync and throws FileSyncException,
    /// so a database on such a mount can no longer be created, converted or checkpointed.
    /// 5.0.21 used FileStream.Flush(true), which on Unix ignores exactly these errnos
    /// (and, per dotnet/runtime#124725, every fsync error), so the same database worked.
    /// The release notes promise the earlier behaviour "for commits, checkpoints and format
    /// conversion" on such storage.
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
                }

                using (var db = new LiteDatabase(file.Filename))
                {
                    db.GetCollection("rows").Count().Should().Be(100);
                    db.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(100);
                }
            }
            finally
            {
                NativeFileSync.SimulateErrno = null;
            }
        }
    }
}
#endif
