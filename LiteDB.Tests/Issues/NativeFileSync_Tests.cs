using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class NativeFileSyncCollection
    {
        public const string Name = "NativeFileSync";
    }

    /// <summary>
    /// Released .NET runtimes lose every Unix fsync error (SystemNative_FSync returns the
    /// comparison instead of the result; dotnet/runtime#124725), so FileStream.Flush(true)
    /// succeeded after EIO. File handles are now synced natively; these tests inject the
    /// errno that native sync reports, so they run on every platform.
    /// </summary>
    [Collection(NativeFileSyncCollection.Name)]
    public class NativeFileSync_Tests
    {
        [Theory]
        [InlineData(22, false, true)]   // EINVAL
        [InlineData(30, false, true)]   // EROFS
        [InlineData(95, false, true)]   // ENOTSUP (Linux)
        [InlineData(45, true, true)]    // ENOTSUP (macOS/BSD)
        [InlineData(102, true, true)]   // EOPNOTSUPP (macOS/BSD)
        [InlineData(45, false, false)]  // EDEADLOCK on Linux, not "unsupported"
        [InlineData(5, false, false)]   // EIO
        [InlineData(28, false, false)]  // ENOSPC
        [InlineData(5, true, false)]
        public void Errno_classification_separates_unsupported_sync_from_failed_sync(int errno, bool bsd, bool unsupported)
        {
            var ex = new FileSyncException("x", errno, bsd);
            ex.IsUnsupported.Should().Be(unsupported);
            ex.HResult.Should().Be(errno);
        }

        [Theory]
        [InlineData(22)]
        [InlineData(95)]
        [InlineData(30)]
        public void Log_that_answers_cannot_sync_degrades_and_keeps_data(int errno)
        {
            using var file = new TempFile();
            using (Inject(path => IsLog(path) ? errno : 0))
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("value");
                rows.Insert(Documents(0, 200));
                db.Checkpoint();
                rows.Update(Documents(0, 200, value: 1));
                db.Checkpoint();
                rows.Insert(Documents(200, 50, value: 1));

                DurableLogFlush(db).Should().BeFalse("storage that cannot sync must be reported, not hidden");
            }

            using var reopened = new LiteDatabase(file.Filename);
            var all = reopened.GetCollection("rows").FindAll().ToList();
            all.Should().HaveCount(250).And.OnlyContain(doc => doc["value"].AsInt32 == 1);
            reopened.GetCollection("rows").Count(Query.EQ("value", 1)).Should().Be(250);
            DurableLogFlush(reopened).Should().BeTrue();
        }

        [Fact]
        public void Failed_log_sync_stops_checkpoint_before_the_data_file_changes()
        {
            using var file = new TempFile();
            var failing = false;
            using (Inject(path => failing && IsLog(path) ? 5 : 0))
            {
                var db = new LiteDatabase(file.Filename);
                try
                {
                    db.CheckpointSize = 0;
                    var rows = db.GetCollection("rows");
                    rows.Insert(Documents(0, 100));
                    DurableLogFlush(db).Should().BeTrue();

                    var before = TempFile.ReadAllBytesShared(file.Filename);
                    failing = true;
                    Action checkpoint = () => db.Checkpoint();
                    checkpoint.Should().Throw<IOException>().Which.HResult.Should().Be(5);
                    TempFile.ReadAllBytesShared(file.Filename).Should().Equal(before, "a failed sync must stop before any data overwrite");
                    failing = false;
                }
                finally
                {
                    failing = false;
                    try { db.Dispose(); } catch (LiteException) { } catch (IOException) { }
                }
            }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").Count().Should().Be(100, "the acknowledged commits stay recoverable from the WAL");
        }

        [Fact]
        public void Failed_commit_sync_is_reported_to_the_writer()
        {
            using var file = new TempFile();
            var failing = false;
            using (Inject(path => failing && IsLog(path) ? 5 : 0))
            {
                var db = new LiteDatabase(file.Filename);
                try
                {
                    var rows = db.GetCollection("rows");
                    rows.Insert(Documents(0, 10));
                    failing = true;
                    Action insert = () => rows.Insert(Documents(10, 10));
                    insert.Should().Throw<Exception>("an unsynced commit must not be acknowledged as durable");
                }
                finally
                {
                    failing = false;
                    try { db.Dispose(); } catch (LiteException) { } catch (IOException) { }
                }
            }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").Count().Should().BeGreaterOrEqualTo(10);
        }

        private static IDisposable Inject(Func<string, int> errno)
        {
            NativeFileSync.SimulateErrno = errno;
            return new Reset();
        }

        private sealed class Reset : IDisposable
        {
            public void Dispose() => NativeFileSync.SimulateErrno = null;
        }

        private static bool IsLog(string path) => path.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase);

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

        private static BsonDocument[] Documents(int start, int count, int value = 0) =>
            Enumerable.Range(start, count).Select(id => new BsonDocument
            {
                ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 200)
            }).ToArray();
    }
}
