#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>Decision 6 after a failure is recorded: nothing more is written or synced (independent review B).</summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class RecordedFailure_Tests
    {
        /// <summary>
        /// A $database read records a data sync that failed with an I/O error without stopping the
        /// engine (its stop is due at the next call). A write whose source is $database (SELECT .. INTO)
        /// then committed its batch in the same operation, and the automatic checkpoint after it retried
        /// the data sync on the handle that failed (fsyncgate: the retry "succeeds"), backfilled the data
        /// file and emptied the WAL. The write is now refused with the recorded failure, before it writes.
        /// </summary>
        [Fact]
        public void Write_in_the_operation_that_recorded_a_failed_data_sync_is_refused()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            using (var setup = new LiteDatabase(file.Filename))
                setup.GetCollection("rows").Insert(Enumerable.Range(1, 16).Select(Row));
            var dataPath = Path.GetFullPath(file.Filename);
            var dataErrno = 0;
            var failedAt = 0;
            var dataSyncs = 0;
            NativeFileSync.SimulateErrno = path =>
            {
                if (!string.Equals(Path.GetFullPath(path), dataPath, StringComparison.OrdinalIgnoreCase)) return 0;
                var n = Interlocked.Increment(ref dataSyncs);
                var errno = dataErrno;
                if (errno == 5) { failedAt = n; dataErrno = 0; } // one EIO; a retry on the handle then "succeeds"
                return errno;
            };
            try
            {
                using var db = new LiteDatabase(file.Filename);
                db.CheckpointSize = 1; // every commit starts an automatic checkpoint
                dataErrno = 22; // cannot sync: the WAL is kept, walKept retries the data sync
                db.GetCollection("rows").Update(Row(1));
                new FileInfo(logName).Length.Should().BeGreaterThan(0);
                dataErrno = 5;
                var files = (SyncPowerLossModel.ReadShared(file.Filename), SyncPowerLossModel.ReadShared(logName));

                Action selectInto = () => db.Execute("SELECT $ INTO dbinfo FROM $database");
                var refused = selectInto.Should().Throw<IOException>().Which;
                refused.Message.Should().StartWith(LiteEngine.WriteFailedPrefix + "A data sync failed");
                refused.Data["LiteDB.CommitOutcome"].Should().Be("NotCommitted");

                failedAt.Should().BeGreaterThan(0, "walKept's data sync failed with EIO and was recorded");
                dataSyncs.Should().Be(failedAt, "no sync is retried on the handle whose sync failed");
                SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(files.Item1, "no checkpoint wrote after the recorded failure");
                SyncPowerLossModel.ReadShared(logName).Should().Equal(files.Item2, "the refused write wrote no frame");
                db.GetCollection("rows").Count().Should().Be(16, "the engine continues read-only");
                db.GetCollection("dbinfo").Count().Should().Be(0);
                db.Execute("SELECT $ FROM $database").Single()["writeFailure"]["operation"].AsString.Should().Be("A data sync");
            }
            finally
            {
                NativeFileSync.SimulateErrno = null;
                File.Delete(logName);
            }
        }

        /// <summary>
        /// An in-memory or temporary database lives in streams its engine's own factories own: the failed
        /// engine's teardown releases them, so a read-only reopen would start from an empty database (it
        /// failed with "cannot be initialized in read-only mode", retried by every call, and its record
        /// claimed a kept log file). It stays closed, as before decision 6, and says why.
        /// </summary>
        [Theory]
        [InlineData(":memory:")]
        [InlineData(":temp:")]
        public void Volatile_database_stays_closed_after_a_write_failure(string filename)
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, 10).Select(Row));
            var armed = true;
            engine.SimulateDiskWriteFail = _ =>
            {
                if (!armed) return;
                armed = false;
                throw new IOException("injected WAL write failure (disk full)");
            };
            Action write = () => rows.Insert(Row(11));
            write.Should().Throw<IOException>();

            for (var attempt = 0; attempt < 2; attempt++)
            {
                Action read = () => rows.FindAll().ToList();
                read.Should().Throw<IOException>().WithMessage("Engine closed after an I/O failure*injected WAL write failure (disk full)")
                    .Which.Message.Should().NotContain("Reopening").And.NotContain("log file");
            }
        }

        private static BsonDocument Row(int id) => new BsonDocument
        {
            ["_id"] = id, ["value"] = id % 5, ["payload"] = new string((char)('a' + id % 26), 1500) + id
        };
    }
}
#endif
