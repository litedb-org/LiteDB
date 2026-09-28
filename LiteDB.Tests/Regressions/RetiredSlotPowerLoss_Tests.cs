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
    /// only after the checkpoint's own proof) publishes its witness root into the OS cache only.
    /// Neither that connection nor an independent one may then make a WAL change durable that
    /// needs the root: a power loss (modelled by keeping each file as of its last successful
    /// sync) must keep every commit acknowledged while durableLogFlush was true.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    [Collection(NativeFileSyncCollection.Name)]
    public class RetiredSlotPowerLoss_Tests
    {
        [Fact]
        public void Retiring_checkpoint_whose_data_sync_fails_keeps_commits_a_power_loss_must_not_lose()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            try
            {
                using var engine = new SharedEngine(power.Settings());
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                var durable = RetireWhileTheDataFileStopsSyncing(engine, db, power);
                DurableLogFlush(db).Should().BeFalse("the data file stopped syncing");

                power.AfterPowerLoss(64).Should().Be(durable, "commits acknowledged as durable survive the power loss");
            }
            finally { EngineState.SimulateProcessCrash = null; }
        }

        /// <summary>
        /// An independent connection opens a fresh engine that knows nothing of the failed sync.
        /// While the data file still cannot sync, it must find that out before it reuses a slot
        /// (the slot's witness is not durable) and must not report its commits durable.
        /// </summary>
        [Fact]
        public void Independent_connection_does_not_reuse_retired_slots_while_the_data_file_cannot_sync()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            try
            {
                using var first = new SharedEngine(power.Settings());
                using var firstDb = new LiteDatabase(first, disposeOnClose: false);
                RetireWhileTheDataFileStopsSyncing(first, firstDb, power);

                var before = SyncPowerLossModel.ReadShared(logName);
                using var second = new SharedEngine(power.Settings());
                using var secondDb = new LiteDatabase(second, disposeOnClose: false);
                Update(secondDb, 10);

                ChangedFrames(before, SyncPowerLossModel.ReadShared(logName)).Should().Be(0, "no retired slot is reused before the data file syncs");
                DurableLogFlush(secondDb).Should().BeFalse("the data file cannot sync");
                power.AfterPowerLoss(64).Should().Be(10);
            }
            finally { EngineState.SimulateProcessCrash = null; }
        }

        /// <summary>
        /// Control for the proof: once the data file syncs again, the independent connection's
        /// data sync makes the witness root durable, so it reuses retired slots and its durable
        /// commits survive the power loss.
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
                RetireWhileTheDataFileStopsSyncing(first, firstDb, power);
                power.DataFails = false;

                var before = SyncPowerLossModel.ReadShared(logName);
                using var second = new SharedEngine(power.Settings());
                using var secondDb = new LiteDatabase(second, disposeOnClose: false);
                Update(secondDb, 10);

                ChangedFrames(before, SyncPowerLossModel.ReadShared(logName)).Should().BeGreaterThan(0, "the control reuses retired slots");
                DurableLogFlush(secondDb).Should().BeTrue();
                power.AfterPowerLoss(64).Should().Be(10, "a durable commit survives the power loss");
            }
            finally { EngineState.SimulateProcessCrash = null; }
        }

        /// <summary>
        /// Commit values 1..9 under a live reader, then run a partial checkpoint during which the
        /// data file stops syncing after the checkpoint proved it could. Returns the last value
        /// acknowledged while durableLogFlush was true.
        /// </summary>
        private static int RetireWhileTheDataFileStopsSyncing(SharedEngine engine, LiteDatabase db, SyncPowerLossModel power)
        {
            db.CheckpointSize = 0;
            var durable = 0;
            for (var value = 1; value <= 5; value++)
            {
                Update(db, value);
                if (DurableLogFlush(db)) durable = value;
            }
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
                    EngineState.SimulateProcessCrash = phase => { if (phase == "checkpoint-before-page-write") power.DataFails = true; };
                    db.Checkpoint();
                    EngineState.SimulateProcessCrash = null;
                });
            }
            power.DataFails.Should().BeTrue("the checkpoint wrote data pages");
            RetirementRoot(SyncPowerLossModel.ReadShared(power.DataFile)).Should().BeGreaterThan(0, "the checkpoint published a witness root (in the OS cache)");
            durable.Should().Be(9);
            return durable;
        }

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
