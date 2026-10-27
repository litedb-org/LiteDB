using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Tests.Engine;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Internals
{
    public class BackgroundCheckpointPowerLoss_Tests
    {
        private readonly ITestOutputHelper _output;
        public BackgroundCheckpointPowerLoss_Tests(ITestOutputHelper output) => _output = output;

        [Theory]
        [InlineData(null, false, false)]
        [InlineData("secret", false, false)]
        [InlineData(null, true, false)]
        [InlineData("secret", true, false)]
        [InlineData(null, true, true)]
        [InlineData("secret", true, true)]
        public void EveryPhysicalWorkerBoundaryLosesUnsyncedBytesAndTearsSectors(string password, bool durable, bool partial)
        {
            IndexMigrationCrashDevice device = null;
            try
            {
                using (var scenario = new BackgroundCheckpointPowerScenario(password, durable, (data, log) =>
                {
                    device = new IndexMigrationCrashDevice(data, log) { Armed = false, TearPrefixes = new[] { 512, 4096 } };
                    return BackgroundCheckpointPowerScenario.Factory(device.Data, device.Log, password);
                }, partial))
                {
                    device.Stage = "background";
                    device.Armed = true;
                    scenario.FinishPass();
                    device.Armed = false;
                    device.Events.Should().Contain(new[] { "background:log.before-sync", "background:data.before-sync",
                        "background:log.torn-write", "background:data.torn-write" });
                    if (!partial) device.Events.Should().Contain("background:log.after-truncate");
                    else device.Events.Should().Contain("background:log.before-write");
                    device.Images.Should().Contain(image => image.Prefix == 512);
                    device.Images.Should().Contain(image => image.Prefix == 4096);
                    _output.WriteLine("Physical boundaries: " + device.PhysicalBoundaries + "; images: " + device.Images.Count);
                    foreach (var image in device.Images)
                    {
                        try { VerifyImage(image.Data, image.Log, password, durable, partial ? 3 : 1); }
                        catch (Exception error) { throw BackgroundCheckpointRecovery.Preserve(image.Data, image.Log,
                            new InvalidOperationException(image.Event + " prefix=" + image.Prefix, error)); }
                    }
                }
            }
            finally { device?.Dispose(); }
        }

        public static IEnumerable<object[]> Failures()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var durable in new[] { false, true })
            foreach (var failure in new[] { "lost", "wal-sync", "journal-sync", "data-sync", "salt-sync", "data-tear", "salt-tear" })
            foreach (var prefix in failure.EndsWith("tear", StringComparison.Ordinal) ? new[] { 512, 4096 } : new[] { 0 })
                yield return new object[] { password, durable, failure, prefix };
        }

        [Theory]
        [MemberData(nameof(Failures))]
        public void WorkerPowerCutOrFailedSyncStopsAndRepeatedRepairRecovers(string password, bool durable, string failure, int prefix)
        {
            PromotionPowerLossStream dataDevice = null, logDevice = null;
            var scenario = new BackgroundCheckpointPowerScenario(password, durable, (data, log) =>
            {
                dataDevice = new PromotionPowerLossStream(data);
                logDevice = new PromotionPowerLossStream(log);
                return BackgroundCheckpointPowerScenario.Factory(dataDevice, logDevice, password);
            });
            using var data = dataDevice;
            using var log = logDevice;
            var hit = false;
            var cause = new IOException("Injected background " + failure);
            Action cut = () =>
            {
                hit = true;
                data.PowerCut();
                log.PowerCut();
                throw cause;
            };
            scenario.Engine.CheckpointStage = stage =>
            {
                if (failure == "lost" && stage == "checkpoint-before-wal-sync") cut();
                if ((failure == "wal-sync" || failure == "journal-sync") && stage == "checkpoint-before-wal-sync")
                {
                    var syncs = 0;
                    log.BeforeFlush = () => { if (++syncs == (failure == "wal-sync" ? 1 : 2)) cut(); };
                }
                if ((failure == "data-sync" && stage == "checkpoint-after-journal-sync") ||
                    (failure == "salt-sync" && stage == "checkpoint-before-salt-write")) data.BeforeFlush = cut;
                if ((failure == "data-tear" && stage == "checkpoint-after-journal-sync") ||
                    (failure == "salt-tear" && stage == "checkpoint-before-salt-write"))
                    data.TearNextWrite = (bytes, offset, count) =>
                    {
                        hit = true;
                        data.TearWrite(bytes, offset, count, prefix, damage: true);
                        log.PowerCut();
                        throw cause;
                    };
            };
            byte[] savedData, savedLog;
            try
            {
                scenario.Release.Set();
                SpinWaitUntil(() => scenario.Worker.HasExited);
                hit.Should().BeTrue("the requested physical fault must execute on the background pass");
                Action nextWrite = () => scenario.Database.GetCollection("rows").Update(BackgroundCheckpointWorkload.Rows(99));
                nextWrite.Should().Throw<Exception>().Which.ToString().Should().Contain(cause.Message);
                savedData = data.DurableBytes;
                savedLog = log.DurableBytes;
            }
            finally
            {
                // Terminal cleanup must not write new durable bytes after the cut.
                Record.Exception(scenario.Dispose).Should().NotBeNull();
            }
            var expected = durable || failure != "lost" && failure != "wal-sync" ? 1 : 0;
            if (failure == "data-tear")
                foreach (var phase in new[] { "promotion-recovery-after-header-flush", "promotion-recovery-before-journal-retire" })
                    InterruptRepair(ref savedData, ref savedLog, password, phase);
            if (expected == 0) VerifyImage(savedData, savedLog, password, durable: false);
            else BackgroundCheckpointRecovery.Verify(savedData, savedLog, password, expected);
        }

        private static void InterruptRepair(ref byte[] dataBytes, ref byte[] logBytes, string password, string phase)
        {
            using var data = new PromotionPowerLossStream(dataBytes);
            using var log = new PromotionPowerLossStream(logBytes);
            var hit = false;
            try
            {
                EngineState.SimulateProcessCrash = current =>
                {
                    if (current != phase) return;
                    hit = true;
                    data.PowerCut();
                    log.PowerCut();
                    throw new IOException("Interrupted background recovery");
                };
                Action open = () =>
                {
                    using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
                };
                open.Should().Throw<IOException>();
                hit.Should().BeTrue();
            }
            finally { EngineState.SimulateProcessCrash = null; }
            dataBytes = data.DurableBytes;
            logBytes = log.DurableBytes;
        }

        private static void VerifyImage(byte[] dataBytes, byte[] logBytes, string password, bool durable, int acknowledged = 1)
        {
            var version = acknowledged;
            if (!durable)
            {
                using var data = ChecksumTestFiles.Copy(dataBytes);
                using var log = ChecksumTestFiles.Copy(logBytes);
                using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password, ReadOnly = true });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                // External ledger permits only the entire acknowledged cache-only
                // update or its entire durable predecessor. Never accept a mixed set.
                version = db.GetCollection("rows").FindById(0)["value"].AsInt32 / 100;
                version.Should().BeInRange(0, 1);
            }
            BackgroundCheckpointRecovery.Verify(dataBytes, logBytes, password, version);
        }

        private static void SpinWaitUntil(Func<bool> predicate) =>
            System.Threading.SpinWait.SpinUntil(predicate, TimeSpan.FromSeconds(20)).Should().BeTrue();
    }
}
