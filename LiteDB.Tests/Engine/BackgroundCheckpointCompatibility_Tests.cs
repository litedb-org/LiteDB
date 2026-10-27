using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class BackgroundCheckpointCompatibility_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void ExistingEmptyWalDoesNotFabricateAPreamblePeak(string password)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password }))
                BackgroundCheckpointIntegrationScenario.Seed(db, 2);
            var log = Path.ChangeExtension(file.Filename, null) + "-log.db";
            File.WriteAllBytes(log, Array.Empty<byte>());
            using (var engine = new LiteEngine(new EngineSettings
            {
                Filename = file.Filename, Password = password, BackgroundCheckpoint = true
            }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Info(db)["peakPhysicalWalBytes"].AsInt64.Should().Be(new FileInfo(log).Length);
                BackgroundCheckpointIntegrationScenario.Verify(db, 0);
            }
            BackgroundCheckpointIntegrationScenario.VerifyRecovery(file.Filename, password, 0);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("secret", false)]
        [InlineData("secret", true)]
        public void DiagnosticQueriesCannotStartWorkAndExactPeakIncludesFooter(string password, bool durable)
        {
            using var scenario = new BackgroundCheckpointIntegrationScenario(password, durable);
            var worker = scenario.Engine.InstallDirectCheckpointLifetime();
            var metrics = scenario.Disk.CheckpointMetrics;
            var log = Path.ChangeExtension(scenario.File.Filename, null) + "-log.db";
            long observed = File.Exists(log) ? new FileInfo(log).Length : 0;
            long logical = 0;
            var callbacks = 0;
            using var completed = new ManualResetEventSlim();
            worker.BeforeCheckpointWorkerPass = () => Interlocked.Increment(ref callbacks);
            worker.AfterCheckpointWorkerPass = completed.Set;
            scenario.Engine.CheckpointStage = stage =>
            {
                observed = Math.Max(observed, new FileInfo(log).Length);
                logical = Math.Max(logical, scenario.Disk.GetFileLength(FileOrigin.Log) / 8192);
            };
            using (worker.Pause())
            {
                for (var version = 1; version <= 7; version++)
                {
                    scenario.Database.UserVersion = version;
                    observed = Math.Max(observed, new FileInfo(log).Length);
                    logical = Math.Max(logical, scenario.Disk.GetFileLength(FileOrigin.Log) / 8192);
                }
                var before = File.ReadAllBytes(log);
                var epoch = worker.RequestEpoch;
                var info = Info(scenario.Database);
                info["enabled"].AsBoolean.Should().BeTrue();
                info["state"].AsString.Should().Be("Paused");
                info["logicalBacklogPages"].AsInt64.Should().Be(7);
                info["admissionWaitCount"].AsInt64.Should().Be(0);
                info["lastFailure"].IsNull.Should().BeTrue();
                callbacks.Should().Be(0);
                info["requestCount"].AsInt64.Should().Be(epoch);
                worker.RequestEpoch.Should().Be(epoch + 1, "ordinary reader disposal emits one state-only unpin notification");
                File.ReadAllBytes(log).Should().Equal(before);
            }
            completed.Wait(TimeSpan.FromSeconds(20)).Should().BeTrue();
            scenario.Database.Checkpoint();
            metrics.PeakPhysicalBytes.Should().Be(observed, "independent OS lengths include the temporary aligned journal");
            metrics.PeakLogicalPages.Should().Be(logical);
            metrics.Passes.Should().BeGreaterThan(0);
            metrics.Requests.Should().BeGreaterThan(0);
            metrics.ActiveMilliseconds.Should().BeGreaterThan(0);
            metrics.PeakPhysicalBytes.Should().BeGreaterThan(7L * 8192 + 16384);
            scenario.VerifyRecovery(7);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task AdmissionDiagnosticsCountActualWaitsWithoutChangingTheirOutcome(string password)
        {
            using var scenario = new BackgroundCheckpointIntegrationScenario(password);
            var worker = scenario.Engine.InstallDirectCheckpointLifetime();
            scenario.Database.Timeout = TimeSpan.FromSeconds(1);
            scenario.Database.Checkpoint();
            using (worker.Pause())
            {
                for (var version = 1; version <= 8; version++) scenario.Database.UserVersion = version;
                var error = await Task.Run(() => Record.Exception(() => scenario.Database.UserVersion = 9));
                error.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);
                var info = Info(scenario.Database);
                info["admissionWaitCount"].AsInt64.Should().Be(1);
                info["admissionWaitMilliseconds"].AsDouble.Should().BeGreaterThan(0);
                info["admissionClosed"].AsBoolean.Should().BeTrue();
                scenario.Database.UserVersion.Should().Be(8);
                scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(8L * 8192);
                scenario.Engine.GetMonitor().Transactions.Should().BeEmpty();
            }
            scenario.Database.Checkpoint();
            scenario.VerifyRecovery(8);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void ReadonlyAndCallerStreamFallbacksExposeDisabledSnapshotsAndPreserveBytes(string password)
        {
            using var scenario = new BackgroundCheckpointIntegrationScenario(password);
            scenario.Database.CheckpointSize = 0;
            for (var version = 1; version <= 150; version++) scenario.Database.UserVersion = version;
            scenario.Engine.Dispose();
            var log = Path.ChangeExtension(scenario.File.Filename, null) + "-log.db";
            var dataBytes = File.ReadAllBytes(scenario.File.Filename);
            var logBytes = File.ReadAllBytes(log);
            using (var engine = new LiteEngine(new EngineSettings
            {
                Filename = scenario.File.Filename, Password = password, ReadOnly = true, BackgroundCheckpoint = true
            }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var info = Info(db);
                info["enabled"].AsBoolean.Should().BeFalse();
                info["state"].AsString.Should().Be("Disabled");
                info["peakPhysicalWalBytes"].IsNull.Should().BeTrue();
                BackgroundCheckpointIntegrationScenario.Verify(db, 150);
            }
            File.ReadAllBytes(scenario.File.Filename).Should().Equal(dataBytes);
            File.ReadAllBytes(log).Should().Equal(logBytes);
            using var data = new MemoryStream();
            using var wal = new MemoryStream();
            using (var engine = new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = wal, Password = password, BackgroundCheckpoint = true
            }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Info(db)["enabled"].AsBoolean.Should().BeFalse();
                db.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(0));
                db.Checkpoint();
                ((object)db.GetCollection("rows").FindById(0)).Should().BeEquivalentTo(BackgroundCheckpointScenario.Row(0));
            }
            BackgroundCheckpointIntegrationScenario.VerifyRecovery(scenario.File.Filename, password, 150);
        }

        private static BsonDocument Info(LiteDatabase db)
        {
            using var result = db.Execute("SELECT $ FROM $database");
            result.Read().Should().BeTrue();
            return result.Current["backgroundCheckpoint"].AsDocument;
        }
    }
}
