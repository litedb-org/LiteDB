using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection(NativeFileSyncCollection.Name)]
    public class BackgroundCheckpointMaintenance_Tests
    {
        [Fact]
        public async Task RebuildJoinsBeforeExclusionAndBindsAFreshIncarnation()
        {
            using var scenario = new DirectCheckpointScenario();
            using var pass = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var joining = new ManualResetEventSlim();
            using var exclusive = new ManualResetEventSlim();
            scenario.Worker.AfterCheckpointWorkerPass = () => { pass.Set(); AutomaticCheckpointWorker_Tests.Wait(release); };
            scenario.Worker.BeforeCheckpointWorkerPauseWait = joining.Set;
            scenario.Engine.SimulateBeforeExclusiveAdmission = exclusive.Set;
            scenario.Worker.Request(scenario.Worker.Incarnation);
            AutomaticCheckpointWorker_Tests.Wait(pass);
            var rebuilding = DirectCheckpointLifetime_Tests.Start(() => scenario.Engine.Rebuild());
            try
            {
                AutomaticCheckpointWorker_Tests.Wait(joining);
                exclusive.IsSet.Should().BeFalse("the worker joins before transaction exclusion or replacement");
                rebuilding.IsCompleted.Should().BeFalse();
            }
            finally { release.Set(); }
            await DirectCheckpointLifetime_Tests.Completes(rebuilding);
            rebuilding.Result.Should().BeNull();
            exclusive.IsSet.Should().BeTrue();
            scenario.Worker.HasExited.Should().BeTrue();
            var replacement = scenario.CurrentWorker;
            replacement.Should().NotBeNull().And.NotBeSameAs(scenario.Worker);
            replacement.Incarnation.Should().NotBe(scenario.Worker.Incarnation);
            replacement.Request(scenario.Worker.Incarnation);
            replacement.RequestEpoch.Should().Be(0, "requests for retired services cannot start work on replacement handles");
            scenario.Worker.Request(scenario.Worker.Incarnation);
            scenario.Worker.State.Should().Be(AutomaticCheckpointState.Stopped);
            // The surviving facade now uses the replacement owner/services.
            scenario.Database.UserVersion = 29;
            scenario.Engine.Dispose();
            replacement.HasExited.Should().BeTrue();
            scenario.VerifyRecovery(userVersion: 29);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailedWorkerJoinOrCloseCheckpointAbortsRebuildBeforeReplacement(bool closeCheckpoint)
        {
            using var scenario = new DirectCheckpointScenario();
            using var pass = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var joining = new ManualResetEventSlim();
            var original = File.ReadAllBytes(scenario.File.Filename);
            var originalLog = File.ReadAllBytes(FileHelper.GetLogFile(scenario.File.Filename));
            var cause = new IOException("maintenance drain failed");
            var dataPages = 0;
            Task<Exception> rebuilding;
            if (closeCheckpoint)
            {
                scenario.Engine.CheckpointStage = stage =>
                {
                    if (stage != "data-page") return;
                    dataPages++;
                    throw cause;
                };
                rebuilding = DirectCheckpointLifetime_Tests.Start(() => scenario.Engine.Rebuild());
            }
            else
            {
                scenario.Worker.BeforeCheckpointWorkerPass = () =>
                {
                    pass.Set();
                    AutomaticCheckpointWorker_Tests.Wait(release);
                    throw cause;
                };
                scenario.Worker.BeforeCheckpointWorkerPauseWait = joining.Set;
                scenario.Worker.Request(scenario.Worker.Incarnation);
                AutomaticCheckpointWorker_Tests.Wait(pass);
                rebuilding = DirectCheckpointLifetime_Tests.Start(() => scenario.Engine.Rebuild());
                try { AutomaticCheckpointWorker_Tests.Wait(joining); }
                finally { release.Set(); }
            }
            await DirectCheckpointLifetime_Tests.Completes(rebuilding);
            rebuilding.Result.Should().BeSameAs(cause);
            Record.Exception(scenario.Engine.Dispose).Should().BeSameAs(cause);
            if (closeCheckpoint) dataPages.Should().Be(1, "the failed close left a partially backfilled, recoverable source pair");
            else
            {
                File.ReadAllBytes(scenario.File.Filename).Should().Equal(original);
                File.ReadAllBytes(FileHelper.GetLogFile(scenario.File.Filename)).Should().Equal(originalLog);
            }
            File.Exists(FileHelper.GetSuffixFile(scenario.File.Filename, "-backup", false)).Should().BeFalse();
            File.Exists(FileHelper.GetSuffixFile(FileHelper.GetLogFile(scenario.File.Filename), "-backup", false)).Should().BeFalse();
            scenario.VerifyRecovery();
        }

        [Fact]
        public async Task CommittedZeroPragmaLetsStartedPassFinishAndSuppressesLaterCloseCheckpoint()
        {
            using var scenario = new DirectCheckpointScenario();
            using var pass = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            var dataPages = 0;
            scenario.Worker.BeforeCheckpointWorkerPass = () => { pass.Set(); AutomaticCheckpointWorker_Tests.Wait(release); };
            scenario.Worker.AfterCheckpointWorkerPass = finished.Set;
            scenario.Engine.CheckpointStage = stage => { if (stage == "data-page") Interlocked.Increment(ref dataPages); };
            scenario.Worker.Request(scenario.Worker.Incarnation);
            AutomaticCheckpointWorker_Tests.Wait(pass);
            var pragma = DirectCheckpointLifetime_Tests.Start(() => scenario.Database.CheckpointSize = 0);
            try
            {
                await DirectCheckpointLifetime_Tests.Completes(pragma);
                pragma.Result.Should().BeNull();
                scenario.Database.CheckpointSize.Should().Be(0);
                scenario.Worker.State.Should().Be(AutomaticCheckpointState.Running);
                dataPages.Should().Be(0);
            }
            finally { release.Set(); }
            AutomaticCheckpointWorker_Tests.Wait(finished);
            SpinWait.SpinUntil(() => scenario.Worker.State == AutomaticCheckpointState.Idle, TimeSpan.FromSeconds(5)).Should().BeTrue();
            dataPages.Should().BeGreaterThan(0, "an already-started pass finishes after CP0 commits");
            scenario.Database.UserVersion = 29;
            scenario.Disk.GetFileLength(FileOrigin.Log).Should().BeGreaterThan(0);
            var pagesBeforeClose = dataPages;
            scenario.Engine.Dispose();
            dataPages.Should().Be(pagesBeforeClose);
            new FileInfo(FileHelper.GetLogFile(scenario.File.Filename)).Length.Should().BeGreaterThan(0);
            scenario.VerifyRecovery(userVersion: 29);
        }
    }
}
