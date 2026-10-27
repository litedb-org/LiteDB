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
    public class BackgroundCheckpointClose_Tests
    {
        [Fact]
        public async Task PublicCheckpointJoinsWorkerOutsideRegistryGateAndReturnsItsOwnCount()
        {
            using var scenario = new DirectCheckpointScenario();
            using var completed = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var joining = new ManualResetEventSlim();
            scenario.Worker.AfterCheckpointWorkerPass = () => { completed.Set(); AutomaticCheckpointWorker_Tests.Wait(release); };
            scenario.Worker.BeforeCheckpointWorkerPauseWait = joining.Set;
            scenario.Worker.Request(scenario.Worker.Incarnation);
            AutomaticCheckpointWorker_Tests.Wait(completed);
            var count = -1;
            var checkpoint = DirectCheckpointLifetime_Tests.Start(() => count = scenario.Engine.Checkpoint());
            try
            {
                AutomaticCheckpointWorker_Tests.Wait(joining);
                checkpoint.IsCompleted.Should().BeFalse();
                Record.Exception(() => { using var peer = new LiteEngine(scenario.File.Filename); })
                    .Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("maintenance");
                using var otherFile = new TempFile();
                var other = DirectCheckpointLifetime_Tests.Start(() => { using var db = new LiteDatabase(otherFile.Filename); });
                await DirectCheckpointLifetime_Tests.Completes(other);
                other.Result.Should().BeNull();
            }
            finally { release.Set(); }
            await DirectCheckpointLifetime_Tests.Completes(checkpoint);
            checkpoint.Result.Should().BeNull();
            count.Should().Be(0, "the worker already drained the WAL; this is the caller's attempt count");
            scenario.VerifyRecovery();
        }

        [Fact]
        public void WorkerStartDoesNotInheritAmbientFacadeReferences()
        {
            using var scenario = new DirectCheckpointScenario();
            using var passed = new ManualResetEventSlim();
            var ambient = new AsyncLocal<LiteEngine>();
            ambient.Value = scenario.Engine;
            LiteEngine observed = scenario.Engine;
            scenario.Worker.BeforeCheckpointWorkerPass = () => observed = ambient.Value;
            scenario.Worker.AfterCheckpointWorkerPass = passed.Set;
            try
            {
                scenario.Worker.Request(scenario.Worker.Incarnation);
                AutomaticCheckpointWorker_Tests.Wait(passed);
                observed.Should().BeNull("an idle process-owned thread must not retain the request's facade through ExecutionContext");
                ambient.Value.Should().BeSameAs(scenario.Engine);
            }
            finally { ambient.Value = null; }
            scenario.Engine.Dispose();
            scenario.Worker.HasExited.Should().BeTrue();
            scenario.VerifyRecovery();
        }

        [Fact]
        public async Task FinalCloseDrainsAnAppendAlreadyInsideWalWriterWithCp0()
        {
            using var scenario = new DirectCheckpointScenario(0);
            using var appending = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var pages = 0;
            scenario.Engine.SimulateCrashPoint = phase =>
            {
                if (phase != "wal-page-before-write" || Interlocked.Increment(ref pages) != 1) return;
                appending.Set();
                AutomaticCheckpointWorker_Tests.Wait(release);
            };
            var writer = DirectCheckpointLifetime_Tests.Start(() => scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(2)));
            AutomaticCheckpointWorker_Tests.Wait(appending);
            var closing = DirectCheckpointLifetime_Tests.Start(scenario.Engine.Dispose);
            try
            {
                SpinWait.SpinUntil(() => scenario.State.Closing, TimeSpan.FromSeconds(5)).Should().BeTrue();
                (await Task.WhenAny(closing, Task.Delay(100))).Should().NotBeSameAs(closing);
                scenario.State.Disposed.Should().BeFalse();
                scenario.Engine.GetMonitor().Transactions.Should().HaveCount(1, "page owners cannot be discarded under an in-flight WAL batch");
            }
            finally { release.Set(); }
            await DirectCheckpointLifetime_Tests.Completes(writer);
            await DirectCheckpointLifetime_Tests.Completes(closing);
            writer.Result.Should().NotBeNull();
            closing.Result.Should().BeNull();
            scenario.VerifyRecovery(uncertain: true);
        }

        [Fact]
        public void ReentrantDisposeRefusesBeforeDroppingFacadeAndCanBeRetried()
        {
            using var scenario = new DirectCheckpointScenario();
            var refusals = 0;
            scenario.Engine.CheckpointStage = stage =>
            {
                if (stage != "data-page") return;
                Record.Exception(scenario.Engine.Dispose).Should().BeOfType<InvalidOperationException>()
                    .Which.Message.Should().Contain("holding storage locks");
                refusals++;
            };
            scenario.Engine.Checkpoint().Should().BeGreaterThan(0);
            refusals.Should().BeGreaterThan(0);
            scenario.State.Disposed.Should().BeFalse();
            scenario.Engine.CheckpointStage = null;
            scenario.Engine.Dispose();
            scenario.State.Disposed.Should().BeTrue();
            scenario.VerifyRecovery();
        }

        [Fact]
        public async Task TerminalPublicationBeforeAcknowledgementRefusesTheCompletedWriter()
        {
            using var scenario = new DirectCheckpointScenario();
            using var transactionReleased = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            scenario.Engine.GetMonitor().AfterTransactionExit = () =>
            {
                transactionReleased.Set();
                AutomaticCheckpointWorker_Tests.Wait(release);
            };
            var cause = new IOException("failure before acknowledgement");
            scenario.Worker.BeforeCheckpointWorkerPass = () => throw cause;
            var writer = DirectCheckpointLifetime_Tests.Start(() => scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(2)));
            try
            {
                AutomaticCheckpointWorker_Tests.Wait(transactionReleased);
                scenario.Worker.Request(scenario.Worker.Incarnation);
                SpinWait.SpinUntil(() => scenario.State.Stopped, TimeSpan.FromSeconds(5)).Should().BeTrue();
            }
            finally { release.Set(); }
            await DirectCheckpointLifetime_Tests.Completes(writer);
            writer.Result.Should().BeOfType<IOException>().Which.InnerException.Should().BeSameAs(cause);
            Record.Exception(scenario.Engine.Dispose).Should().BeSameAs(cause);
            scenario.VerifyRecovery(uncertain: true);
        }

        [Fact]
        public void ExplicitFinalDisposeSurfacesCloseCheckpointErrorAfterResourceCleanup()
        {
            using var scenario = new DirectCheckpointScenario();
            var cause = new IOException("close checkpoint failed");
            scenario.Engine.CheckpointStage = stage => { if (stage == "data-page") throw cause; };
            Record.Exception(scenario.Engine.Dispose).Should().BeSameAs(cause);
            Record.Exception(scenario.Engine.Dispose).Should().BeSameAs(cause);
            scenario.State.Disposed.Should().BeTrue();
            scenario.State.TerminalCause.Should().BeSameAs(cause);
            scenario.VerifyRecovery();
        }
    }
}
