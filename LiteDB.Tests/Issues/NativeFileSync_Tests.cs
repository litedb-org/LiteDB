using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
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

        // errno values differ between Linux and macOS/BSD (ENOTSUP is 95 vs 45); inject
        // the ones native sync reports for "cannot sync" on the running platform.
        public static TheoryData<int> UnsupportedErrnos()
        {
            var bsd = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX) ||
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Create("FREEBSD"));
            var data = new TheoryData<int> { 22, 30 }; // EINVAL, EROFS
            if (bsd)
            {
                data.Add(45);  // ENOTSUP
                data.Add(102); // EOPNOTSUPP
            }
            else data.Add(95); // ENOTSUP
            return data;
        }

        /// <summary>
        /// Opted out of durable commits, a log that answers "cannot sync" is not a failure (proposed
        /// default A of docs/decisions/durability-policy.md): commits and checkpoints degrade to
        /// ordered OS-cache flushes, as before, and $database reports the weaker guarantee.
        /// </summary>
        [Theory]
        [MemberData(nameof(UnsupportedErrnos))]
        public void Log_that_answers_cannot_sync_degrades_and_keeps_data_without_durable_commits(int errno)
        {
            using var file = new TempFile();
            using (Inject(path => IsLog(path) ? errno : 0))
            using (var db = new LiteDatabase($"Filename={file.Filename};durable commits=false"))
            {
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("value");
                rows.Insert(Documents(0, 200));
                db.Checkpoint();
                rows.Update(Documents(0, 200, value: 1));
                db.Checkpoint();
                rows.Insert(Documents(200, 50, value: 1));

                DurableLogFlush(db).Should().BeFalse("storage that cannot sync must be reported, not hidden");
                WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");
            }

            using var reopened = new LiteDatabase(file.Filename);
            var all = reopened.GetCollection("rows").FindAll().ToList();
            all.Should().HaveCount(250).And.OnlyContain(doc => doc["value"].AsInt32 == 1);
            reopened.GetCollection("rows").Count(Query.EQ("value", 1)).Should().Be(250);
            DurableLogFlush(reopened).Should().BeTrue();
        }

        /// <summary>
        /// With durable commits (the default) the same answer fails loudly (decision 3): the proof before
        /// the engine's first commit finds that the log cannot sync, so that commit throws before it
        /// writes a frame. The data file stays byte for byte, the log holds nothing, reads return exactly
        /// the earlier rows, $database reports the failure, and the next write throws with it (decision
        /// 6). Reopened on storage that syncs, the database holds exactly the earlier rows and writes.
        /// </summary>
        [Theory]
        [MemberData(nameof(UnsupportedErrnos))]
        public void Log_that_answers_cannot_sync_refuses_a_durable_commit_before_it_writes(int errno)
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("rows").Insert(Documents(0, 200));
            }
            DurableLogs.Forget(Path.GetFullPath(logName)); // as in a new process: its syncs are not known
            var data = File.ReadAllBytes(file.Filename);
            var logSyncs = 0;

            using (Inject(path => { if (!IsLog(path)) return 0; logSyncs++; return errno; }))
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                Action insert = () => rows.Insert(Documents(200, 50, value: 1));
                insert.Should().Throw<IOException>().WithMessage(WriteFailureAssert.LogNotWritten + "*")
                    .Which.InnerException.Should().BeOfType<FileSyncException>().Which.Errno.Should().Be(errno);
                logSyncs.Should().Be(1, "the proof asked once");
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                LogLength(logName).Should().Be(0, "no frame was written");

                rows.FindAll().Select(doc => doc["_id"].AsInt32).Should().Equal(Enumerable.Range(0, 200));
                rows.Count(Query.EQ("value", 0)).Should().Be(200);
                var reason = WriteFailureAssert.CommitRefused(db);
                WriteFailureAssert.Refused(() => rows.EnsureIndex("value"), reason);
                logSyncs.Should().Be(1, "a refused write asks the storage nothing");
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                LogLength(logName).Should().Be(0);
            }

            using var reopened = new LiteDatabase(file.Filename);
            WriteFailureAssert.NoneRecorded(reopened, "a reopen retries");
            reopened.GetCollection("rows").FindAll().Select(doc => doc["_id"].AsInt32).Should().Equal(Enumerable.Range(0, 200));
            reopened.GetCollection("rows").Insert(Documents(200, 50, value: 1));
            reopened.GetCollection("rows").Count().Should().Be(250);
            DurableLogFlush(reopened).Should().BeTrue();
        }

        private static long LogLength(string logName) => File.Exists(logName) ? new FileInfo(logName).Length : 0;

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

        [Fact]
        public void Unix_binds_a_c_library_so_syncs_report_their_errors()
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                NativeFileSync.UsesRuntimeSync.Should().BeFalse("Windows syncs through FlushFileBuffers, which throws");
                return;
            }

            NativeLibc.LibraryName.Should().NotBeNull("without a bound C library every fsync error would be lost");
            NativeLibc.TryGet(out var fsync, out _).Should().BeTrue();
            fsync(-1).Should().Be(-1, "an invalid descriptor must reach the real fsync");
            System.Runtime.InteropServices.Marshal.GetLastWin32Error().Should().Be(9, "EBADF");
            NativeFileSync.UsesRuntimeSync.Should().BeFalse();
        }

        [Fact]
        public void FileStream_subclass_that_overrides_flush_keeps_control_of_the_sync()
        {
            using var file = new TempFile();
            using var stream = new CountingFileStream(file.Filename);
            stream.WriteByte(1);
            stream.FlushToDisk();
            stream.DurableFlushes.Should().Be(1, "a caller's own Flush(bool) defines what a device sync means");
        }

        private sealed class CountingFileStream : FileStream
        {
            internal int DurableFlushes;

            internal CountingFileStream(string path) : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite) { }

            public override void Flush(bool flushToDisk)
            {
                if (flushToDisk) DurableFlushes++;
                base.Flush(flushToDisk);
            }
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
