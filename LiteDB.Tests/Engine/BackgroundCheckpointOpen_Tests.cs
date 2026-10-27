using System;
using System.IO;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Engine
{
    public class BackgroundCheckpointOpen_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void RecoveredHighTailClosesAdmissionBeforeFirstWriterAndQueuesMaintenance(string password)
        {
            using var source = new BackgroundCheckpointIntegrationScenario(password);
            using var image = new TempFile();
            source.Database.Timeout = TimeSpan.FromSeconds(1);
            source.Database.Checkpoint();
            var worker = source.Engine.InstallDirectCheckpointLifetime();
            using (worker.Pause())
            {
                for (var value = 1; value <= 8; value++) source.Database.UserVersion = value;
                source.Disk.GetFileLength(FileOrigin.Log).Should().Be(8L * PAGE_SIZE);
                File.WriteAllBytes(image.Filename, TempFile.ReadAllBytesShared(source.File.Filename));
                File.WriteAllBytes(FileHelper.GetLogFile(image.Filename),
                    TempFile.ReadAllBytesShared(FileHelper.GetLogFile(source.File.Filename)));
            }
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var engine = new LiteEngine(new EngineSettings
            {
                Filename = image.Filename, Password = password, DurableCommits = false, BackgroundCheckpoint = true,
                CheckpointStage = stage =>
                {
                    if (stage != "before-commit-lock") return;
                    entered.Set();
                    AutomaticCheckpointWorker_Tests.Wait(release);
                }
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var disk = BackgroundCheckpointIntegrationScenario.DiskOf(engine);
            try
            {
                AutomaticCheckpointWorker_Tests.Wait(entered);
                disk.WriteAdmission.UnderPressure.Should().BeTrue();
                disk.AutomaticCheckpoint.RequestEpoch.Should().BeGreaterThan(0);
                disk.GetFileLength(FileOrigin.Log).Should().Be(8L * PAGE_SIZE);
                engine.GetMonitor().Transactions.Should().BeEmpty();
                Record.Exception(() => engine.BeginTrans()).Should().BeOfType<LiteException>()
                    .Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);
                engine.GetMonitor().Transactions.Should().BeEmpty();
                disk.WriteAdmission.Writers.Should().Be(0);
                disk.GetFileLength(FileOrigin.Log).Should().Be(8L * PAGE_SIZE);
            }
            finally { release.Set(); }
            db.Checkpoint();
            disk.WriteAdmission.UnderPressure.Should().BeFalse();
            BackgroundCheckpointIntegrationScenario.Verify(db, 8);
            engine.Dispose();
            BackgroundCheckpointIntegrationScenario.VerifyRecovery(image.Filename, password, 8);
        }

        [Fact]
        public void DefaultOpenStaysSynchronousAndInternalOptInRebindsAfterRebuild()
        {
            using var source = new BackgroundCheckpointIntegrationScenario();
            source.Disk.AutomaticCheckpoint.Should().BeNull();
            source.Disk.WriteAdmission.Should().BeNull();
            source.Disk.CheckpointBarrier.Should().BeNull();
            source.Engine.Dispose();
            using var engine = new LiteEngine(new EngineSettings
            {
                Filename = source.File.Filename, DurableCommits = false, BackgroundCheckpoint = true
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var old = engine.InstallDirectCheckpointLifetime();
            old.State.Should().Be(AutomaticCheckpointState.Idle);
            engine.Rebuild();
            var next = BackgroundCheckpointIntegrationScenario.DiskOf(engine).AutomaticCheckpoint;
            old.HasExited.Should().BeTrue();
            next.Should().NotBeNull().And.NotBeSameAs(old);
            next.Request(old.Incarnation);
            next.RequestEpoch.Should().Be(0);
            using (next.Pause())
            {
                for (var version = 1; version <= 3; version++) db.UserVersion = version;
                next.RequestEpoch.Should().BeGreaterThan(0, "the rebound core automatically routes new commits");
            }
            db.Checkpoint();
            engine.Dispose();
            BackgroundCheckpointIntegrationScenario.VerifyRecovery(source.File.Filename, null, 3);
        }

        [Theory]
        [InlineData("readonly")]
        [InlineData("bypass")]
        [InlineData("shared")]
        [InlineData("signals")]
        [InlineData("volatile")]
        [InlineData("memory")]
        [InlineData("temp")]
        [InlineData("stream")]
        public void IneligibleOpenCannotAcquireWorkerAuthority(string kind)
        {
            using var source = new BackgroundCheckpointIntegrationScenario(checkpoint: 0);
            source.Engine.Dispose();
            var before = TempFile.ReadAllBytesShared(source.File.Filename);
            using var stream = new MemoryStream();
            var signals = new RecordingSignals();
            var settings = new EngineSettings
            {
                Filename = source.File.Filename, DurableCommits = false, BackgroundCheckpoint = true,
                ReadOnly = kind == "readonly", BypassDirectHost = kind == "bypass",
                SharedMode = kind == "shared", CheckpointEachCommit = kind == "volatile",
                CoordinationSignals = kind == "signals" ? signals : null
            };
            if (kind == "memory") settings.Filename = ":memory:";
            if (kind == "temp") settings.Filename = ":temp:";
            if (kind == "stream") settings.DataStream = stream;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var disk = BackgroundCheckpointIntegrationScenario.DiskOf(engine);
                disk.AutomaticCheckpoint.Should().BeNull();
                disk.WriteAdmission.Should().BeNull();
                disk.CheckpointBarrier.Should().BeNull();
                Record.Exception(() => engine.InstallDirectCheckpointLifetime()).Should().BeOfType<InvalidOperationException>();
                if (kind == "readonly") BackgroundCheckpointIntegrationScenario.Verify(db, 0);
                else
                {
                    db.UserVersion = 7;
                    if (kind == "signals") signals.Commits.Should().BeGreaterThan(0);
                    if (kind == "volatile")
                    {
                        db.CheckpointSize.Should().Be(0);
                        disk.GetFileLength(FileOrigin.Log).Should().Be(0,
                            "CheckpointEachCommit remains synchronous even with a stored zero pragma");
                    }
                }
            }
            if (kind == "readonly") TempFile.ReadAllBytesShared(source.File.Filename).Should().Equal(before);
            if (kind == "readonly" || kind == "bypass" || kind == "shared" || kind == "signals" || kind == "volatile")
                BackgroundCheckpointIntegrationScenario.VerifyRecovery(source.File.Filename, null, kind == "readonly" ? 0 : 7);
        }

        private sealed class RecordingSignals : ICoordinationSignals
        {
            internal int Commits;
            public void StructuralBegin() { }
            public void StructuralEnd(int version) { }
            public void SlotReused() { }
            public void Committed(int version) { Commits++; }
        }
    }
}
