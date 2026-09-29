#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Decision 6 of docs/decisions/durability-policy.md (a real write or sync failure is sticky: it is
    /// recorded, the engine reopens read-only on its next call, reads keep working, and every later
    /// write throws with the record) where the first implementation left gaps: failures that stopped
    /// the engine without a record (it stayed closed, so reads threw, against decision 2), a data sync
    /// that failed outside a checkpoint, races with the reopen, and diagnostics after it. Refusals
    /// before anything was written (the data file cannot sync, implementation note 6) are no failure.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public partial class StickyFailure_Tests
    {
        private const int Rows = 16;
        private const int EIO = 5, EINVAL = 22;

        /// <summary>
        /// A transaction past its page limit writes safepoints to the WAL. One whose first frame write
        /// fails before any byte reached the log stopped the engine without a record, so it stayed
        /// closed and even reads threw. It is now recorded on the log file: the engine reads the
        /// acknowledged row, reports the failure, and refuses writes with it, changing neither stream.
        /// </summary>
        [Fact]
        public void Safepoint_write_that_fails_before_its_frame_is_recorded_and_reads_keep_working()
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, TransactionPageLimit = 1 });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "acknowledged" });
            var failure = new IOException("injected safepoint write failure");
            var armed = true;
            engine.SimulateDiskWriteFail = _ =>
            {
                if (!armed) return;
                armed = false;
                throw failure;
            };
            Action write = () => rows.Insert(Enumerable.Range(2, 20).Select(id => new BsonDocument { ["_id"] = id, ["p"] = new string('p', 3000) }));
            write.Should().Throw<IOException>().Which.Should().BeSameAs(failure);

            rows.FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1);
            var record = ReadOnlyAfterWriteFailure.AssertReported(db, "A write", "log", failure.Message, walKept: true);
            var files = (Data: data.ToArray(), Log: log.ToArray());
            ReadOnlyAfterWriteFailure.AssertWriteRefused(() => rows.Insert(new BsonDocument { ["_id"] = 100 }), record)
                .InnerException.Should().BeSameAs(failure);
            data.ToArray().Should().Equal(files.Data);
            log.ToArray().Should().Equal(files.Log);
            rows.FindById(1)["value"].AsString.Should().Be("acknowledged");
        }

        /// <summary>
        /// Past the WAL limit a write first retries the data sync. When that sync failed (an I/O error,
        /// not "cannot sync") the write threw it, but nothing was recorded and the engine kept writing.
        /// It is now a sticky failure: recorded on the data file, the engine continues read-only with
        /// every row, and refuses writes with it.
        /// </summary>
        [Fact]
        public void Data_sync_that_fails_at_the_wal_limit_is_recorded_and_the_engine_continues_read_only()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            var connection = $"Filename={file.Filename};wal limit=1MB";
            using (var setup = new LiteDatabase(connection)) setup.GetCollection("rows").EnsureIndex("value"); // its header synced
            using var syncs = new Syncs(file.Filename) { Data = EINVAL };
            try
            {
                using var db = new LiteDatabase(connection);
                var count = 0;
                while (Info(db)["logFileSize"].AsInt64 <= 1024 * 1024) db.GetCollection("rows").Insert(Row(++count));
                syncs.Data = EIO;
                Action insert = () => db.GetCollection("rows").Insert(Row(count + 1));
                var failure = insert.Should().Throw<IOException>().Which;
                failure.Message.Should().NotStartWith("Cannot modify", "the data sync failed, it did not answer \"cannot sync\"");

                var files = (SyncPowerLossModel.ReadShared(file.Filename), SyncPowerLossModel.ReadShared(logName));
                db.GetCollection("rows").Count().Should().Be(count);
                var record = ReadOnlyAfterWriteFailure.AssertReported(db, "A data sync", "data", failure.Message, walKept: true);
                ReadOnlyAfterWriteFailure.AssertWriteRefused(insert, record).InnerException.Should().BeSameAs(failure);
                SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(files.Item1);
                SyncPowerLossModel.ReadShared(logName).Should().Equal(files.Item2);
                db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(Enumerable.Range(1, count));
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// $database.walKept retries the data sync of a writable engine that has not seen one succeed.
        /// When that sync failed with an I/O error, the $database read threw it (decision 2: reads keep
        /// working). The read now reports it (readOnly, writeFailure on the data file, walKept), the next
        /// call reopens the engine read-only, and writes throw it.
        /// </summary>
        [Fact]
        public void Database_read_whose_data_sync_fails_reports_the_failure_instead_of_throwing_it()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var syncs = new Syncs(file.Filename);
            try
            {
                using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                syncs.Data = EINVAL;
                Update(db, 1); // durable in the WAL
                Info(db)["walKept"].AsBoolean.Should().BeTrue("the data file cannot sync");
                syncs.Data = EIO;

                BsonDocument info = null;
                Action read = () => info = Info(db);
                read.Should().NotThrow("a $database read does not fail for a failed data sync");
                info["walKept"].AsBoolean.Should().BeTrue();
                info["readOnly"].AsBoolean.Should().BeTrue();
                var failure = info["writeFailure"].AsDocument;
                failure["operation"].AsString.Should().Be("A data sync");
                failure["file"].AsString.Should().Be("data");
                info["readOnlyReason"].AsString.Should().StartWith("A data sync failed at");

                // The read could not stop the engine; its stop is due, and the next call makes it.
                var failed = engine.GetState();
                failed.StopDue.Should().BeTrue();
                failed.Stopped.Should().BeFalse();
                SyncPowerLossModel.AssertRows(db, Rows, 1);
                failed.Stopped.Should().BeTrue("the next call stopped the engine whose failure the read recorded");
                engine.GetState().Should().NotBeSameAs(failed, "and reopened it read-only");
                var record = ReadOnlyAfterWriteFailure.AssertReported(db, "A data sync", "data", failure["error"].AsString, walKept: true);
                ReadOnlyAfterWriteFailure.AssertWriteRefused(() => Update(db, 2), record);
                SyncPowerLossModel.AssertRows(db, Rows, 1);
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// Implementation note 6: a retiring checkpoint whose promotion is refused before it wrote
        /// anything (the data file cannot sync) is no failure. When an automatic checkpoint after a
        /// commit hit it, the refusal was recorded and the engine went read-only; recorded no longer, it
        /// must not throw from that commit either (decision 5). Writes stay durable in the WAL.
        /// </summary>
        [Fact]
        public void Automatic_checkpoint_refused_before_it_wrote_is_neither_thrown_nor_recorded()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
                setup.GetCollection("rows").Insert(Enumerable.Range(1, 64).Select(id => MvccRetirementScenario.Document(id, 0)));
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                using var power = new FilePowerLossModel(file.Filename);
                using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                for (var value = 1; value <= 5; value++) Update(db, value, 64);
                Exception thrown = null;
                using (var reader = engine.Query("rows", new Query()))
                {
                    reader.Read().Should().BeTrue();
                    var thread = new Thread(() => thrown = Record.Exception(() =>
                    {
                        for (var value = 6; value <= 9; value++) Update(db, value, 64);
                        var data = SyncPowerLossModel.ReadShared(file.Filename);
                        power.DataFailsFromSync = power.DataSyncs + 2; // the retirement proof's data sync, then the promotion's
                        db.CheckpointSize = 1; // this pragma's commit runs a retiring checkpoint: the reader keeps the WAL
                        power.DataSyncs.Should().BeGreaterOrEqualTo(power.DataFailsFromSync, "the promotion tried its data sync");
                        SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "the refused promotion wrote nothing");
                    }));
                    thread.Start();
                    thread.Join();
                }
                thrown.Should().BeNull("the commit succeeded, and its checkpoint's refusal is no failure");
                Info(db)["writeFailure"].IsNull.Should().BeTrue();
                Info(db)["readOnly"].AsBoolean.Should().BeFalse();
                Update(db, 10, 64);
                Info(db)["durableLogFlush"].AsBoolean.Should().BeTrue();
                SyncPowerLossModel.AssertRows(db, 64, 10);
                power.AfterPowerLoss(x => SyncPowerLossModel.AssertRows(x, 64, 10));
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// $database after the read-only reopen: walKept was false for a direct connection (only a
        /// shared connection's state was consulted) although the failure kept the WAL; it now reports
        /// the recorded failure's. durableLogFlush read true after the log's write failed; it is false.
        /// </summary>
        [Fact]
        public void Database_reports_the_kept_wal_and_the_failed_log_after_the_reopen()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                using (var power = new FilePowerLossModel(file.Filename))
                {
                    var armed = false;
                    var settings = new EngineSettings
                    {
                        Filename = file.Filename,
                        CheckpointStage = stage => { if (armed && stage == "data-page") power.DataFails = true; }
                    };
                    using var db = new LiteDatabase(new LiteEngine(settings));
                    db.CheckpointSize = 0;
                    Update(db, 1);
                    db.CheckpointSize = 1;
                    armed = true;
                    Update(db, 2); // its automatic checkpoint fails after writing a page
                    var info = Info(db);
                    info["writeFailure"]["walKept"].AsBoolean.Should().BeTrue();
                    info["walKept"].AsBoolean.Should().BeTrue("the failure kept the WAL, and the read-only engine removes nothing");
                    info["durableLogFlush"].AsBoolean.Should().BeTrue("the data file failed, not the log");
                }
                using (var drain = new LiteDatabase(file.Filename)) drain.Checkpoint(); // the data file syncs again
                DurableLogs.Forget(Path.GetFullPath(logName));
                using (var power = new FilePowerLossModel(file.Filename) { LogFails = true })
                using (var db = new LiteDatabase(file.Filename))
                {
                    Action insert = () => Update(db, 3);
                    insert.Should().Throw<IOException>().WithMessage("This commit was not written: the log file cannot sync*");
                    var info = Info(db);
                    info["writeFailure"]["file"].AsString.Should().Be("log");
                    info["durableLogFlush"].AsBoolean.Should().BeFalse("the log's sync failed");
                    SyncPowerLossModel.AssertRows(db, Rows, 2);
                }
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// A file marked for rebuild (by another engine's damaged-file stop) next to an engine whose write
        /// failed, with auto-rebuild: the read-only reopen (and a shared connection's next read-only
        /// operation) opens the file as it is. A rebuild would replace the files from an engine that may
        /// not write.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Read_only_reopen_after_a_write_failure_never_rebuilds(bool shared)
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            DurableLogs.Forget(Path.GetFullPath(logName));
            var directory = Path.GetDirectoryName(file.Filename);
            var name = Path.GetFileNameWithoutExtension(file.Filename);
            try
            {
                using var power = new FilePowerLossModel(file.Filename) { LogFails = true };
                using var db = new LiteDatabase($"Filename={file.Filename};auto-rebuild=true" + (shared ? ";Connection=shared" : ""));
                Action insert = () => Update(db, 1);
                insert.Should().Throw<IOException>().WithMessage("This commit was not written*");

                MarkForRebuild(file.Filename);
                var marked = SyncPowerLossModel.ReadShared(file.Filename);
                SyncPowerLossModel.AssertRows(db, Rows, 0);
                Info(db)["readOnly"].AsBoolean.Should().BeTrue();
                SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(marked, "the read-only engine rebuilt nothing");
                Directory.GetFiles(directory, name + "*").Where(x => x.Contains("backup")).Should().BeEmpty();
            }
            finally
            {
                File.Delete(logName);
                foreach (var backup in Directory.GetFiles(directory, name + "*").Where(x => x.Contains("backup"))) File.Delete(backup);
            }
        }

        /// <summary>
        /// Evidence for a reported path that cannot be reached: a vector write needs no format promotion
        /// on a checksummed file (v10 and later are past the vector format, v9), and a legacy file is
        /// converted before it is written, which needs a data sync. So a vector write on a legacy file
        /// whose data file cannot sync is refused by the read-only open (the engine stays usable); and
        /// once converted, a vector write commits, durable in the WAL, also while the data file cannot sync.
        /// </summary>
        [Fact]
        public void Vector_write_to_a_legacy_file_whose_data_file_cannot_sync_is_refused_and_reads_keep_working()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, UnsyncedReadOnlyOpen_Tests.Fixture("plain.db"));
            var logName = FileHelper.GetLogFile(file.Filename);
            var vector = new BsonDocument { ["_id"] = 1, ["v"] = new BsonVector(new[] { 1f, 2f }) };
            try
            {
                using var power = new FilePowerLossModel(file.Filename) { DataFails = true };
                using (var db = new LiteDatabase(file.Filename))
                {
                    Action write = () => db.GetCollection("vectors").Insert(vector);
                    UnsyncedReadOnlyOpen_Tests.AssertWriteRefused(write, UnsyncedReadOnlyOpen_Tests.ConversionRefused);
                    UnsyncedReadOnlyOpen_Tests.AssertPlainRows(db);
                    Info(db)["writeFailure"].IsNull.Should().BeTrue("a refusal is no failure");
                    UnsyncedReadOnlyOpen_Tests.AssertWriteRefused(write, UnsyncedReadOnlyOpen_Tests.ConversionRefused);
                    UnsyncedReadOnlyOpen_Tests.AssertPlainRows(db);
                }
                power.DataFails = false;
                using (var db = new LiteDatabase(file.Filename))
                {
                    power.DataFails = true;
                    Header(file.Filename)[HeaderPage.P_FILE_VERSION].Should().BeGreaterThan(HeaderPage.VECTOR_FILE_VERSION);
                    db.GetCollection("vectors").Insert(vector);
                    db.GetCollection("vectors").FindById(1)["v"].AsVector.Should().Equal(1f, 2f);
                    Info(db)["writeFailure"].IsNull.Should().BeTrue();
                    UnsyncedReadOnlyOpen_Tests.AssertPlainRows(db);
                }
            }
            finally { File.Delete(logName); }
        }

        private static void MarkForRebuild(string filename)
        {
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[PAGE_SIZE];
            stream.ReadFully(header, 0, PAGE_SIZE);
            header[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
            PageChecksum.Write(new BufferSlice(header, 0, PAGE_SIZE));
            stream.Position = 0;
            stream.Write(header, 0, PAGE_SIZE);
        }

        private static void Setup(string filename)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            setup.GetCollection("rows").EnsureIndex("value");
        }

        private static void Update(LiteDatabase db, int value, int rows = Rows) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, rows).Select(id => MvccRetirementScenario.Document(id, value)));

        private static BsonDocument Row(int id) => new BsonDocument
        {
            ["_id"] = id, ["value"] = id % 5, ["payload"] = new string((char)('a' + id % 26), 4000) + id
        };

        private static byte[] Header(string filename) => SyncPowerLossModel.ReadShared(filename).Take(PAGE_SIZE).ToArray();

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();

        /// <summary>The errno each file's syncs answer (0: they succeed): EINVAL is "cannot sync" (#2242), EIO a failed sync.</summary>
        private sealed class Syncs : IDisposable
        {
            private readonly string _data, _log;
            internal volatile int Data, Log;

            internal Syncs(string filename)
            {
                _data = Path.GetFullPath(filename);
                _log = Path.GetFullPath(FileHelper.GetLogFile(filename));
                NativeFileSync.SimulateErrno = path =>
                {
                    var name = Path.GetFullPath(path);
                    if (string.Equals(name, _data, StringComparison.OrdinalIgnoreCase)) return Data;
                    if (string.Equals(name, _log, StringComparison.OrdinalIgnoreCase)) return Log;
                    return 0;
                };
            }

            public void Dispose() => NativeFileSync.SimulateErrno = null;
        }
    }
}
#endif
