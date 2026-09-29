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
    /// A retiring checkpoint during which the data file stops syncing (#2242 degradation found
    /// only after the checkpoint's own proof) published its witness root into the OS cache only.
    /// Neither that connection nor an independent one may then make a WAL change durable that
    /// needs the root: a power loss (modelled by keeping each file as of its last successful
    /// sync) must keep every commit acknowledged while durableLogFlush was true. Such a checkpoint
    /// now stops ("stopped syncing") before it retires a frame or removes its header journal, and
    /// records the failure before its engine stops (decision 6 of docs/decisions/durability-policy.md).
    /// An engine that opens over that journal cannot recover until the data file syncs: its open is
    /// refused. So no connection reuses a slot or acknowledges a commit meanwhile.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    [Collection(NativeFileSyncCollection.Name)]
    public class RetiredSlotPowerLoss_Tests
    {
        /// <summary>
        /// Every commit acknowledged durable survives, with the WAL as last synced or as written
        /// back. The failed operation's engine stops (decision 6 of docs/decisions/durability-policy.md);
        /// once the data file syncs again, a new connection recovers every commit and writes again.
        /// </summary>
        [Theory]
        [InlineData(BeforeBackfill)] // found before the root is published: none is
        [InlineData(AtRootPublication)] // the root reaches the OS cache only
        public void Retiring_checkpoint_whose_data_sync_fails_keeps_commits_a_power_loss_must_not_lose(string stage)
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            try
            {
                using var engine = new SharedEngine(power.Settings());
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                var durable = RetireWhileTheDataFileStopsSyncing(engine, db, power, stage);
                var kept = SyncPowerLossModel.ReadShared(logName);
                power.AssertAfterPowerLoss(64, durable); // commits acknowledged as durable survive the power loss
                FilePowerLossModel.Open((power.Capture().Data, kept), image => SyncPowerLossModel.AssertRows(image, 64, durable));

                // The failed operation's engine stopped (terminal stop at this layer; the connection-wide
                // read-only continuation is the continuation layer's).
                power.DataFails = false;

                using var reopened = new SharedEngine(power.Settings());
                using var reopenedDb = new LiteDatabase(reopened, disposeOnClose: false);
                SyncPowerLossModel.AssertRows(reopenedDb, 64, durable);
                Update(reopenedDb, 10);
                SyncPowerLossModel.AssertRows(reopenedDb, 64, 10);
                reopenedDb.GetCollection("$database").FindAll().Single()["writeFailure"].IsNull.Should().BeTrue("a reopen retries");
                DurableLogFlush(reopenedDb).Should().BeTrue();
                power.AssertAfterPowerLoss(64, 10); // acknowledged durable
            }
            finally { EngineState.SimulateProcessCrash = null; }
        }

        /// <summary>
        /// Control for the proof: once the data file syncs again, the independent connection's
        /// open recovers the header (its data sync makes the witness root durable), so it reuses
        /// retired slots and its durable commits survive the power loss.
        /// </summary>
        [Fact]
        public void Independent_connection_reuses_retired_slots_only_after_the_data_file_synced()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            try
            {
                using var first = new SharedEngine(power.Settings());
                using var firstDb = new LiteDatabase(first, disposeOnClose: false);
                RetireWhileTheDataFileStopsSyncing(first, firstDb, power, AtRootPublication);
                power.DataFails = false;

                var before = SyncPowerLossModel.ReadShared(logName);
                using var second = new SharedEngine(power.Settings());
                using var secondDb = new LiteDatabase(second, disposeOnClose: false);
                Update(secondDb, 10);

                ChangedFrames(before, SyncPowerLossModel.ReadShared(logName)).Should().BeGreaterThan(0, "the control reuses retired slots");
                DurableLogFlush(secondDb).Should().BeTrue();
                power.AssertAfterPowerLoss(64, 10); // a durable commit survives the power loss
            }
            finally { EngineState.SimulateProcessCrash = null; }
        }

        /// <summary>
        /// The WAL stops syncing after the checkpoint's proof, at its retirement records: publishing
        /// their root in a data file that still syncs would make a durable header name records a
        /// power loss dropped, and the database could not be opened. The root is not published.
        /// The checkpoint keeps the frames it would have retired, and every commit acknowledged before
        /// the log stopped syncing survives a power loss.
        /// </summary>
        [Fact]
        public void Retiring_checkpoint_whose_log_stops_syncing_publishes_no_root()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            var settings = power.Settings();
            settings.CheckpointStage = stage => { if (stage == "retirement-before-record-write") power.LogFails = true; };
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            for (var value = 1; value <= 5; value++) Update(db, value);
            using (var reader = engine.Query("rows", new Query()))
            {
                reader.Read().Should().BeTrue();
                Worker(() =>
                {
                    for (var value = 6; value <= 9; value++) Update(db, value);
                    DurableLogFlush(db).Should().BeTrue("commit 9 is acknowledged durable");
                    // The retirement's own log barrier answers "cannot sync": the checkpoint keeps the frames it
                    // would have retired and publishes no root. (With fail-loud durable commits it throws.)
                    db.Checkpoint();
                });
            }
            power.LogFails.Should().BeTrue("the checkpoint reached its retirement records");
            RetirementRoot(SyncPowerLossModel.ReadShared(power.DataFile)).Should().Be(0, "no root names records that may not be durable");
            power.AfterPowerLoss(64).Should().Be(9, "every commit acknowledged before the log stopped syncing survives");

            power.AfterPowerLoss(64).Should().Be(9);
        }

        /// <summary>
        /// Commit values 1..9 under a live reader, then run a partial checkpoint during which the
        /// data file stops syncing after the checkpoint proved it could: the checkpoint throws
        /// "stopped syncing" and stops its engine, recording the failure, before it retires a frame or
        /// removes its header journal. Returns the last value acknowledged while durableLogFlush was true.
        /// </summary>
        private static int RetireWhileTheDataFileStopsSyncing(SharedEngine engine, LiteDatabase db, SyncPowerLossModel power, string stage)
        {
            db.CheckpointSize = 0;
            var durable = 0;
            for (var value = 1; value <= 5; value++)
            {
                Update(db, value);
                if (DurableLogFlush(db)) durable = value;
            }
            var logName = FileHelper.GetLogFile(power.DataFile);
            using (var reader = engine.Query("rows", new Query()))
            {
                reader.Read().Should().BeTrue();
                Worker(() =>
                {
                    for (var value = 6; value <= 9; value++)
                    {
                        Update(db, value);
                        if (DurableLogFlush(db)) durable = value;
                    }
                    EngineState.SimulateProcessCrash = phase => { if (phase == stage) power.DataFails = true; };
                    power.RetirementStage = stage;
                    try
                    {
                        Action checkpoint = () => db.Checkpoint();
                        checkpoint.Should().Throw<IOException>().WithMessage("The data file stopped syncing*");
                    }
                    finally
                    {
                        EngineState.SimulateProcessCrash = null;
                        power.RetirementStage = null;
                    }
                });
            }
            power.DataFails.Should().BeTrue("the checkpoint reached " + stage);
            var root = RetirementRoot(SyncPowerLossModel.ReadShared(power.DataFile));
            if (stage == BeforeBackfill) root.Should().Be(0, "a checkpoint that lost its data sync before the root publishes none");
            else root.Should().BeGreaterThan(0, "the witness root reached the OS cache");
            BlankFrames(SyncPowerLossModel.ReadShared(logName)).Should().Be(0, "no slot is cleared");
            durable.Should().Be(9);
            return durable;
        }

        private static int BlankFrames(byte[] log) => Enumerable.Range(0, log.Length / WalChecksum.FrameSize)
            .Count(frame => log.Skip(frame * WalChecksum.FrameSize).Take(WalChecksum.FrameSize).All(value => value == 0));

        private const string BeforeBackfill = "checkpoint-before-page-write";
        private const string AtRootPublication = "retirement-before-header-write";

        private static void Setup(string filename)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, 64).Select(id => MvccRetirementScenario.Document(id, 0)));
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, 64).Select(id => MvccRetirementScenario.Document(id, value)));

        /// <summary>Frames that existed before and were rewritten since (a reused slot).</summary>
        private static int ChangedFrames(byte[] before, byte[] after) => Enumerable.Range(0, before.Length / WalChecksum.FrameSize)
            .Count(frame => !before.Skip(frame * WalChecksum.FrameSize).Take(WalChecksum.FrameSize)
                .SequenceEqual(after.Skip(frame * WalChecksum.FrameSize).Take(WalChecksum.FrameSize)));

        private static long RetirementRoot(byte[] data) => BitConverter.ToInt64(data, WalRetirement.RootPosition);

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

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
    }
}
#endif
