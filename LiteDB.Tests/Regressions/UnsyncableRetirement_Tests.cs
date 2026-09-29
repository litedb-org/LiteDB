#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Since data barriers degrade on storage that answers "cannot sync" (#2242), MVCC retirement
    /// must not publish witnesses or clear WAL slots that such storage cannot make durable, and
    /// no engine may reuse slots that were cleared without durable syncs.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    [Collection(NativeFileSyncCollection.Name)]
    public class UnsyncableRetirement_Tests
    {
        /// <summary>
        /// Retirement witnesses need a durable sync and a reused WAL slot a durable clear. Every
        /// retiring checkpoint first syncs the data file and the log (the log's directory once per
        /// engine), so a partial checkpoint under a live reader on storage that answers "cannot sync"
        /// neither retires frames (no v13 promotion, no witness root, no cleared slot) nor lets
        /// later commits reuse slots - also when that checkpoint is the first to find out, and when
        /// commits opted out of syncs. Control: storage that syncs retires and reuses slots. With
        /// durable commits, a WAL directory that cannot sync fails the first commit before it writes
        /// (decision 9 of docs/decisions/durability-policy.md), so nothing is written, retired or
        /// reused there at all.
        /// </summary>
        [Theory]
        [InlineData("syncs")]
        [InlineData("data-detected-earlier")]  // by a checkpoint before the reader
        [InlineData("data-detected-by-partial")]
        [InlineData("opted-out-commits")]      // DurableCommits=false: nothing synced before the checkpoint
        [InlineData("opted-out-log")]          // same, only the WAL cannot sync: found by the proof's log sync
        [InlineData("directory")]              // the WAL's directory cannot be synced (EACCES)
        [InlineData("opted-out-directory")]    // same, found only by the checkpoint's proof
        public void Storage_that_cannot_sync_neither_retires_nor_reuses_wal_frames(string mode)
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
            {
                setup.GetCollection("rows").Insert(Enumerable.Range(1, 8).Select(id => MvccRetirementScenario.Document(id, 0)));
            }
            var version = Header(file.Filename)[HeaderPage.P_FILE_VERSION];
            version.Should().BeLessThan(HeaderPage.MVCC_FILE_VERSION);

            if (mode.StartsWith("data")) NativeFileSync.SimulateErrno = path => path.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase) ? 0 : 22;
            if (mode == "opted-out-commits") NativeFileSync.SimulateErrno = _ => 22;
            if (mode == "opted-out-log") NativeFileSync.SimulateErrno = path => path.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase) ? 22 : 0;
            if (mode.EndsWith("directory")) NativeFileSync.SimulateDirectoryErrno = _ => 13;
            // As in a new process: the setup's engine proved the log's directory synced.
            if (mode == "directory") DurableLogs.Forget(Path.GetFullPath(FileHelper.GetLogFile(file.Filename)));
            try
            {
                using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, DurableCommits = !mode.StartsWith("opted-out") });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                if (mode == "directory") AssertDurableCommitRefusedBeforeItWrites(db, file.Filename);
                else RetireUnderAReader(engine, db, file.Filename, mode);

                var header = Header(file.Filename);
                var promoted = header[HeaderPage.P_FILE_VERSION] == HeaderPage.MVCC_FILE_VERSION;
                var root = BitConverter.ToInt64(header, WalRetirement.RootPosition);
                if (mode == "syncs") promoted.Should().BeTrue("the control retires frames");
                else
                {
                    promoted.Should().BeFalse("no retirement is prepared without durable syncs");
                    root.Should().Be(0, "no retirement witness is published without durable syncs");
                }
            }
            finally
            {
                NativeFileSync.SimulateErrno = null;
                NativeFileSync.SimulateDirectoryErrno = null;
            }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).Should().HaveCount(8)
                .And.OnlyContain(x => x == (mode == "directory" ? 0 : 13));
        }

        /// <summary>
        /// Commit values 2..9 (1 and a checkpoint first for "data-detected-earlier"), checkpoint
        /// under a live reader, then commit 10..13: storage that cannot sync clears no frame and the
        /// later commits append; storage that syncs reuses retired slots.
        /// </summary>
        private static void RetireUnderAReader(LiteEngine engine, LiteDatabase db, string filename, string mode)
        {
            db.CheckpointSize = 0;
            if (mode == "data-detected-earlier")
            {
                Update(db, 1);
                db.Checkpoint();
            }
            for (var value = 2; value <= 5; value++) Update(db, value);

            using var reader = engine.Query("rows", new Query());
            Worker(() =>
            {
                for (var value = 6; value <= 8; value++) Update(db, value);
                var start = LogFileSize(db);
                Update(db, 9);
                var perUpdate = LogFileSize(db) - start;
                perUpdate.Should().BeGreaterThan(0);

                engine.Checkpoint();
                if (mode != "syncs") BlankFrames(ReadShared(FileHelper.GetLogFile(filename))).Should().Be(0,
                    "no committed frame is cleared without durable syncs");
                var before = LogFileSize(db);
                for (var value = 10; value <= 13; value++) Update(db, value);
                var growth = LogFileSize(db) - before;
                if (mode == "syncs") growth.Should().BeLessThan(4 * perUpdate, "syncing storage reuses retired slots");
                else growth.Should().Be(4 * perUpdate, "commits after a checkpoint that could not sync append");
            });
        }

        /// <summary>
        /// Decision 9 of docs/decisions/durability-policy.md: with durable commits, a WAL directory that
        /// cannot be synced (EACCES) fails the first commit loudly, before it writes a frame. The
        /// failure is recorded (decision 6): the engine reads every row, reports the failure in
        /// $database, and refuses every later write with it; neither changes a file.
        /// </summary>
        private static void AssertDurableCommitRefusedBeforeItWrites(LiteDatabase db, string filename)
        {
            var logName = FileHelper.GetLogFile(filename);
            byte[] Log() => File.Exists(logName) ? ReadShared(logName) : Array.Empty<byte>();
            var files = (Data: ReadShared(filename), Log: Log());

            Action commit = () => Update(db, 1);
            var failure = commit.Should().Throw<IOException>()
                .WithMessage("This commit was not written: the log file's directory cannot sync to the device (#2242)*").Which;
            ReadShared(filename).Should().Equal(files.Data, "the commit failed before it wrote");
            Log().Should().Equal(files.Log, "no frame was written");

            db.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).Should().HaveCount(8).And.OnlyContain(x => x == 0);
            var record = ReadOnlyAfterWriteFailure.AssertReported(db, "A commit", "log",
                "This commit was not written: the log file's directory cannot sync", walKept: files.Log.Length > 0);
            ReadOnlyAfterWriteFailure.AssertWriteRefused(() => Update(db, 2), record).InnerException.Should().BeSameAs(failure);
            ReadShared(filename).Should().Equal(files.Data, "the refused write changes nothing");
            Log().Should().Equal(files.Log);
        }

        /// <summary>
        /// A long-lived engine retired frames while its storage synced; then the data file or the
        /// log stops syncing (commits opted out of syncs, so only a checkpoint can find out). Every
        /// retiring checkpoint proves its syncs again, so the next one retires nothing: the witness
        /// root and the cleared slots stay as they were.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Storage_that_stops_syncing_after_a_retirement_retires_nothing_more(bool logStops)
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
                setup.GetCollection("rows").Insert(Enumerable.Range(1, 8).Select(id => MvccRetirementScenario.Document(id, 0)));
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, DurableCommits = false });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                for (var value = 1; value <= 5; value++) Update(db, value);
                using (var reader = engine.Query("rows", new Query()))
                {
                    Worker(() =>
                    {
                        for (var value = 6; value <= 9; value++) Update(db, value);
                        engine.Checkpoint();
                    });
                    var root = BitConverter.ToInt64(Header(file.Filename), WalRetirement.RootPosition);
                    root.Should().BeGreaterThan(0, "syncing storage retires frames");
                    var cleared = BlankFrames(ReadShared(logName));

                    NativeFileSync.SimulateErrno = path => path.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase) == logStops ? 22 : 0;
                    Worker(() =>
                    {
                        for (var value = 10; value <= 13; value++) Update(db, value);
                        engine.Checkpoint();
                    });
                    BitConverter.ToInt64(Header(file.Filename), WalRetirement.RootPosition).Should().Be(root, "no witness is published once storage cannot sync");
                    BlankFrames(ReadShared(logName)).Should().BeLessOrEqualTo(cleared, "no further slot is cleared");
                }
            }
            finally { NativeFileSync.SimulateErrno = null; }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).Should().HaveCount(8).And.OnlyContain(x => x == 13);
        }

        /// <summary>
        /// Shared mode: a data file that stops syncing during a partial checkpoint is found only
        /// after that checkpoint proved it and retired WAL frames. The witness root may then be in
        /// the OS cache only, so the checkpoint must not clear the retired frames, and later
        /// operations of the connection, fresh engines whose own log syncs succeed, must not reuse
        /// those slots. The checkpoint now stops ("stopped syncing") with its header journal kept,
        /// and records the failure (decision 6 of docs/decisions/durability-policy.md): the
        /// connection's next operation cannot recover the kept journal while the data file cannot
        /// sync and opens read-only, so its write is refused with the WAL unchanged, and it keeps
        /// reading every commit. Once the data file syncs, a new connection recovers and writes again.
        /// RetiredSlotPowerLoss_Tests covers other connections and power loss.
        /// </summary>
        [Theory]
        [InlineData("checkpoint-before-page-write")]   // found before the root is published: none is
        [InlineData("retirement-before-header-write")] // the root reaches the OS cache only
        public void Shared_connection_never_reuses_slots_retired_while_the_data_file_stopped_syncing(string stage)
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            using (var setup = new LiteDatabase(file.Filename))
                setup.GetCollection("rows").Insert(Enumerable.Range(1, 64).Select(id => MvccRetirementScenario.Document(id, 0)));

            var dataFails = false;
            NativeFileSync.SimulateErrno = path => path.EndsWith("-log.db", StringComparison.OrdinalIgnoreCase) || !dataFails ? 0 : 22;
            EngineState.SimulateProcessCrash = phase => { if (phase == stage) dataFails = true; };
            try
            {
                using var engine = new SharedEngine(new EngineSettings
                {
                    Filename = file.Filename, CheckpointStage = phase => { if (phase == stage) dataFails = true; }
                });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                for (var value = 1; value <= 5; value++) Update64(db, value);
                byte[] retired;
                var frames = 0;
                using (var reader = engine.Query("rows", new Query()))
                {
                    reader.Read().Should().BeTrue();
                    Worker(() =>
                    {
                        for (var value = 6; value <= 9; value++) Update64(db, value);
                        // Frames written before the checkpoint; the witness records it appends are
                        // discarded by the next open when no root names them.
                        frames = ReadShared(logName).Length / WalChecksum.FrameSize;
                        Action checkpoint = () => db.Checkpoint();
                        checkpoint.Should().Throw<IOException>().WithMessage("The data file stopped syncing*");
                    });
                    retired = ReadShared(logName);
                }
                EngineState.SimulateProcessCrash = null;
                dataFails.Should().BeTrue("the checkpoint reached " + stage);
                var root = BitConverter.ToInt64(Header(file.Filename), WalRetirement.RootPosition);
                if (stage == "checkpoint-before-page-write") root.Should().Be(0, "a checkpoint that lost its data sync before the root publishes none");
                else root.Should().BeGreaterThan(0, "the witness root reached the OS cache");
                BlankFrames(retired).Should().Be(0, "no slot is cleared while the witness root may not be durable");
                frames.Should().BeGreaterThan(0);

                // The connection's next operation opens a fresh engine over the kept header journal: it
                // cannot recover it while the data file cannot sync, so it opens read-only. (Carrying the
                // record itself to the connection's later operations is a later layer's.)
                var data = ReadShared(file.Filename);
                UnsyncedReadOnlyOpen_Tests.AssertWriteRefused(() => Update64(db, 10), UnsyncedReadOnlyOpen_Tests.RecoveryRefused);
                SyncPowerLossModel.AssertRows(db, 64, 9); // the connection keeps reading
                ReadShared(logName).Should().Equal(retired, "slots retired by a checkpoint whose data sync failed must not be reused");
                ReadShared(file.Filename).Should().Equal(data);

                dataFails = false;

                using var reconnected = new LiteDatabase($"Filename={file.Filename};Connection=shared");
                SyncPowerLossModel.AssertRows(reconnected, 64, 9);
                Update64(reconnected, 10); // a new connection retries: its open recovers
                SyncPowerLossModel.AssertRows(reconnected, 64, 10);
                DurableLogFlush(reconnected).Should().BeTrue("the data file syncs again");
                reconnected.GetCollection("$database").FindAll().Single()["writeFailure"].IsNull.Should().BeTrue();
            }
            finally
            {
                EngineState.SimulateProcessCrash = null;
                NativeFileSync.SimulateErrno = null;
            }

            using var reopened = new LiteDatabase(file.Filename);
            SyncPowerLossModel.AssertRows(reopened, 64, 10);
        }

        private static void Update64(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, 64).Select(id => MvccRetirementScenario.Document(id, value)));

        private static int BlankFrames(byte[] log) => BlankOffsets(log).Length;

        private static int[] BlankOffsets(byte[] log) => Enumerable.Range(0, log.Length / WalChecksum.FrameSize)
            .Select(frame => frame * WalChecksum.FrameSize).Where(offset => IsBlank(log, offset)).ToArray();

        private static bool IsBlank(byte[] log, int offset) =>
            log.Skip(offset).Take(WalChecksum.FrameSize).All(value => value == 0);

        private static byte[] ReadShared(string filename)
        {
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadFully(bytes, 0, bytes.Length);
            return bytes;
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
