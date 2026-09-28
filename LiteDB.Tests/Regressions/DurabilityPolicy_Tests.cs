#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// The owner's durability decisions (docs/decisions/durability-policy.md): a commit that cannot be
    /// made durable fails loudly before it writes (3); a write or sync failure is sticky: the engine
    /// keeps reading, refuses every later write with the recorded failure, and $database reports it
    /// without a write (6); a checkpoint's failure after a successful commit does not throw from that
    /// commit or from Dispose (5, 6).
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class DurabilityPolicy_Tests
    {
        private const int Rows = 16;

        /// <summary>
        /// A log that cannot sync (#2242) with durable commits: the commit was acknowledged with
        /// durableLogFlush false. It now throws before it writes a frame, the files stay byte for
        /// byte, reads return every row, $database reports the failure, and the next write throws
        /// with it. With "durable commits=false" the same storage commits, as the caller opted out.
        /// </summary>
        [Fact]
        public void Commit_on_a_log_that_cannot_sync_throws_before_it_writes_a_frame()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            DurableLogs.Forget(Path.GetFullPath(logName)); // as in a new process: its syncs are not known
            var data = File.ReadAllBytes(file.Filename);
            try
            {
                using (var power = new FilePowerLossModel(file.Filename) { LogFails = true })
                using (var db = new LiteDatabase(file.Filename))
                {
                    Action insert = () => db.GetCollection("rows").Insert(MvccRetirementScenario.Document(Rows + 1, 0));
                    insert.Should().Throw<IOException>().WithMessage("This commit was not written: the log file cannot sync*");
                    File.ReadAllBytes(file.Filename).Should().Equal(data);
                    LogLength(logName).Should().Be(0, "no frame was written");

                    SyncPowerLossModel.AssertRows(db, Rows, 0);
                    var info = Info(db);
                    info["readOnly"].AsBoolean.Should().BeTrue();
                    var failure = info["writeFailure"].AsDocument;
                    failure["operation"].AsString.Should().Be("A commit");
                    failure["file"].AsString.Should().Be("log");
                    failure["error"].AsString.Should().StartWith("This commit was not written");
                    failure["walKept"].AsBoolean.Should().BeFalse("the log held no frame");
                    info["readOnlyReason"].AsString.Should().StartWith("A commit failed at");

                    Action again = () => db.GetCollection("rows").Insert(MvccRetirementScenario.Document(Rows + 2, 0));
                    again.Should().Throw<IOException>().Which.Message.Should().StartWith(LiteEngine.WriteFailedPrefix + "A commit failed at");
                    SyncPowerLossModel.AssertRows(db, Rows, 0);
                    File.ReadAllBytes(file.Filename).Should().Equal(data);
                    LogLength(logName).Should().Be(0);
                }

                using (var power = new FilePowerLossModel(file.Filename) { LogFails = true })
                using (var db = new LiteDatabase($"Filename={file.Filename};durable commits=false"))
                {
                    db.GetCollection("rows").Insert(MvccRetirementScenario.Document(Rows + 1, 0));
                    var info = Info(db);
                    info["durableLogFlush"].AsBoolean.Should().BeFalse();
                    info["writeFailure"].IsNull.Should().BeTrue("\"cannot sync\" is the reason to opt out, not a failure");
                    db.GetCollection("rows").Count().Should().Be(Rows + 1);
                }
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// The proof before an engine's first commit (the log file and its directory sync) runs once
        /// per log path in the process: a later engine over the same log commits with one log sync,
        /// as before, so storage that syncs pays for the proof once.
        /// </summary>
        [Fact]
        public void Log_is_proven_once_per_path_in_the_process()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = Path.GetFullPath(FileHelper.GetLogFile(file.Filename));
            DurableLogs.Forget(logName);
            var logSyncs = 0;
            NativeFileSync.SimulateErrno = path =>
            {
                if (string.Equals(Path.GetFullPath(path), logName, StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref logSyncs);
                return 0;
            };
            try
            {
                int First(LiteDatabase db, int id)
                {
                    var before = logSyncs;
                    db.GetCollection("rows").Insert(MvccRetirementScenario.Document(id, 0));
                    return logSyncs - before;
                }
                using (var db = new LiteDatabase(file.Filename)) First(db, Rows + 1).Should().Be(2, "the proof, then the commit's own sync");
                using (var db = new LiteDatabase(file.Filename)) First(db, Rows + 2).Should().Be(1, "the path was proven");
            }
            finally
            {
                NativeFileSync.SimulateErrno = null;
                File.Delete(logName);
            }
        }

        /// <summary>
        /// An automatic checkpoint after a successful commit, whose data sync fails once it wrote:
        /// the checkpoint's exception reached the committing caller, and the engine closed, so even
        /// reads threw. Now the commit returns (it succeeded), reads keep returning every committed
        /// row, $database reports the checkpoint's failure, the next write throws with it before it
        /// changes anything, and Dispose does not throw. With durable commits every acknowledged
        /// commit survives a power loss; either way the files reopen with every row once the
        /// storage syncs.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Checkpoint_failure_after_a_commit_is_recorded_not_thrown(bool durableCommits)
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
                        Filename = file.Filename, DurableCommits = durableCommits,
                        CheckpointStage = stage => { if (armed && stage == "data-page") power.DataFails = true; }
                    };
                    var db = new LiteDatabase(new LiteEngine(settings));
                    db.CheckpointSize = 0;
                    for (var value = 1; value <= 2; value++) Update(db, value);
                    db.CheckpointSize = 1;
                    armed = true;
                    Update(db, 3); // its automatic checkpoint fails after writing a page
                    power.DataFails.Should().BeTrue("the checkpoint wrote a page");

                    SyncPowerLossModel.AssertRows(db, Rows, 3);
                    var failure = Info(db)["writeFailure"].AsDocument;
                    failure["operation"].AsString.Should().Be("A checkpoint");
                    failure["file"].AsString.Should().Be("data");
                    failure["error"].AsString.Should().StartWith("The data file stopped syncing");
                    failure["walKept"].AsBoolean.Should().BeTrue();

                    var dataNow = File.ReadAllBytes(file.Filename);
                    var logNow = SyncPowerLossModel.ReadShared(logName);
                    Action write = () => Update(db, 4);
                    write.Should().Throw<IOException>().Which.Message.Should().StartWith(LiteEngine.WriteFailedPrefix + "A checkpoint failed at");
                    File.ReadAllBytes(file.Filename).Should().Equal(dataNow);
                    SyncPowerLossModel.ReadShared(logName).Should().Equal(logNow);
                    SyncPowerLossModel.AssertRows(db, Rows, 3);

                    Action dispose = () => db.Dispose();
                    dispose.Should().NotThrow();
                    if (durableCommits) power.AfterPowerLoss(x => SyncPowerLossModel.AssertRows(x, Rows, 3));
                }
                using var reopened = new LiteDatabase(file.Filename);
                SyncPowerLossModel.AssertRows(reopened, Rows, 3);
                Info(reopened)["writeFailure"].IsNull.Should().BeTrue("a reopen retries");
                Update(reopened, 4);
                SyncPowerLossModel.AssertRows(reopened, Rows, 4);
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// Another thread's commit (its WAL write fails) while this thread's explicit transaction is open. The
        /// failure ended that transaction: its Commit throws with the recorded failure (it found no
        /// transaction and returned false after the reopen), none of its changes are visible, and
        /// reads return exactly the committed rows.
        /// </summary>
        [Fact]
        public void Explicit_transaction_ended_by_another_threads_failure_throws_at_commit()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                Update(db, 1);

                db.BeginTrans().Should().BeTrue();
                db.GetCollection("pending").Insert(new BsonDocument { ["_id"] = 1 });

                var failing = new Thread(() =>
                {
                    engine.SimulateDiskWriteFail = _ =>
                    {
                        if (Thread.CurrentThread.Name == "failing") throw new IOException("injected WAL write failure");
                    };
                    Action commit = () => Update(db, 2);
                    commit.Should().Throw<IOException>().WithMessage("injected WAL write failure");
                }) { Name = "failing" };
                failing.Start();
                failing.Join();

                Action complete = () => db.Commit();
                complete.Should().Throw<IOException>().Which.Message.Should().StartWith(LiteEngine.WriteFailedPrefix + "A commit failed at");
                db.Rollback().Should().BeFalse("the transaction is completed");
                db.CollectionExists("pending").Should().BeFalse();
                SyncPowerLossModel.AssertRows(db, Rows, 1);
                Info(db)["writeFailure"]["operation"].AsString.Should().Be("A commit");
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// A shared connection opens an engine per operation: a failure of one operation is kept
        /// connection-wide, so later operations open read-only, read every row, report the failure
        /// in $database and refuse writes with it. A new connection retries.
        /// </summary>
        [Fact]
        public void Shared_connection_keeps_a_write_failure_until_it_is_reopened()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            DurableLogs.Forget(Path.GetFullPath(logName));
            try
            {
                using (var power = new FilePowerLossModel(file.Filename) { LogFails = true })
                using (var db = new LiteDatabase($"Filename={file.Filename};Connection=shared"))
                {
                    Action insert = () => Update(db, 1);
                    insert.Should().Throw<IOException>().WithMessage("This commit was not written: the log file cannot sync*");
                    power.LogFails = false; // the next operations' engines would sync now: the failure still holds
                    SyncPowerLossModel.AssertRows(db, Rows, 0);
                    var info = Info(db);
                    info["readOnly"].AsBoolean.Should().BeTrue();
                    info["writeFailure"]["operation"].AsString.Should().Be("A commit");
                    Action again = () => Update(db, 1);
                    again.Should().Throw<IOException>().Which.Message.Should().StartWith(LiteEngine.WriteFailedPrefix);
                    SyncPowerLossModel.AssertRows(db, Rows, 0);
                }
                using (var db = new LiteDatabase($"Filename={file.Filename};Connection=shared"))
                {
                    Update(db, 1);
                    SyncPowerLossModel.AssertRows(db, Rows, 1);
                    Info(db)["writeFailure"].IsNull.Should().BeTrue();
                }
            }
            finally { File.Delete(logName); }
        }

        /// <summary>The "wal limit" option: parsed with size units, written back, and a limit that is not positive is refused.</summary>
        [Fact]
        public void Wal_limit_option_is_parsed_and_validated()
        {
            new ConnectionString("Filename=x.db").WalLimit.Should().Be(EngineSettings.DEFAULT_WAL_LIMIT);
            var cs = new ConnectionString("Filename=x.db;wal limit=10MB");
            cs.WalLimit.Should().Be(10L * 1024 * 1024);
            new ConnectionString(cs.ToString()).WalLimit.Should().Be(10L * 1024 * 1024);
            new ConnectionString("Filename=x.db").ToString().Should().NotContain("wal limit");

            using var file = new TempFile();
            Action open = () => new LiteEngine(new EngineSettings { Filename = file.Filename, WalLimit = 0 }).Dispose();
            open.Should().Throw<ArgumentOutOfRangeException>();
        }

        private static void Setup(string filename)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            setup.GetCollection("rows").EnsureIndex("value");
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

        private static long LogLength(string logName) => File.Exists(logName) ? new FileInfo(logName).Length : 0;

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();
    }
}
#endif
