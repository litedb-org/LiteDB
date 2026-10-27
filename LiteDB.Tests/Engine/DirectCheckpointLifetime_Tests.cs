using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Direct;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection(NativeFileSyncCollection.Name)]
    public class DirectCheckpointLifetime_Tests
    {
        [Theory]
        [InlineData("queued")]
        [InlineData("data-page")]
        [InlineData("data-flushed")]
        public async Task TwoFacadesShareWorkerAndFinalCloseDrainsBeforeReadmission(string stage)
        {
            using var scenario = new DirectCheckpointScenario();
            var peer = new LiteEngine(scenario.File.Filename);
            peer.InstallDirectCheckpointLifetime().Should().BeSameAs(scenario.Worker);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            IDisposable pause = stage == "queued" ? scenario.Worker.Pause() : null;
            scenario.Engine.CheckpointStage = current =>
            {
                if (current != stage) return;
                entered.Set();
                AutomaticCheckpointWorker_Tests.Wait(release);
            };
            Task<Exception> closing = null;
            try
            {
                scenario.Worker.Request(scenario.Worker.Incarnation);
                if (pause == null) AutomaticCheckpointWorker_Tests.Wait(entered);
                scenario.Engine.Dispose(); // non-final: must neither join nor cancel peer work
                scenario.State.Disposed.Should().BeFalse();
                scenario.Worker.State.Should().Be(pause == null ? AutomaticCheckpointState.Running : AutomaticCheckpointState.Paused);
                closing = Start(peer.Dispose);
                SpinWait.SpinUntil(() => scenario.State.Closing, TimeSpan.FromSeconds(5)).Should().BeTrue();
                if (pause == null)
                {
                    closing.IsCompleted.Should().BeFalse();
                    scenario.State.Disposed.Should().BeFalse();
                    Record.Exception(() => { using var refused = new LiteEngine(scenario.File.Filename); })
                        .Should().BeOfType<InvalidOperationException>();
                }
                // Another file must open while this host is draining outside Gate.
                using var otherFile = new TempFile();
                var other = Start(() => { using var db = new LiteDatabase(otherFile.Filename); db.GetCollection("x").Insert(new BsonDocument()); });
                await Completes(other);
                other.Result.Should().BeNull();
            }
            finally { release.Set(); pause?.Dispose(); }
            await Completes(closing);
            closing.Result.Should().BeNull();
            peer.Dispose();
            scenario.Worker.State.Should().Be(AutomaticCheckpointState.Stopped);
            scenario.State.Disposed.Should().BeTrue();
            scenario.Worker.HasExited.Should().BeTrue();
            scenario.VerifyRecovery();
        }

        [Theory]
        [InlineData("before-pass")]
        [InlineData("data-page")]
        [InlineData("after-pass")]
        public async Task WorkerFailureReachesEveryFacadeAndRepeatedExplicitDispose(string boundary)
        {
            using var scenario = new DirectCheckpointScenario();
            var peer = new LiteEngine(scenario.File.Filename);
            using var failed = new ManualResetEventSlim();
            var cause = new IOException("direct worker " + boundary);
            Action fault = () => { failed.Set(); throw cause; };
            if (boundary == "before-pass") scenario.Worker.BeforeCheckpointWorkerPass = fault;
            if (boundary == "after-pass") scenario.Worker.AfterCheckpointWorkerPass = fault;
            scenario.Engine.CheckpointStage = stage => { if (stage == boundary) fault(); };
            scenario.Worker.Request(scenario.Worker.Incarnation);
            AutomaticCheckpointWorker_Tests.Wait(failed);
            SpinWait.SpinUntil(() => scenario.State.Stopped, TimeSpan.FromSeconds(5)).Should().BeTrue();
            foreach (var facade in new[] { scenario.Engine, peer })
            {
                Record.Exception(() => facade.Checkpoint()).Should().BeOfType<IOException>().Which.InnerException.Should().BeSameAs(cause);
                Record.Exception(() => facade.BeginTrans()).Should().BeOfType<IOException>().Which.InnerException.Should().BeSameAs(cause);
                Record.Exception(() => facade.Insert("rows", new[] { BackgroundCheckpointScenario.Row(2) }, BsonAutoId.Int32))
                    .Should().BeOfType<IOException>().Which.InnerException.Should().BeSameAs(cause);
            }
            Record.Exception(() => { using var refused = new LiteEngine(scenario.File.Filename); }).Should().BeOfType<IOException>();
            Record.Exception(peer.Dispose).Should().BeSameAs(cause);
            Record.Exception(peer.Dispose).Should().BeSameAs(cause);
            var final = Start(scenario.Engine.Dispose);
            await Completes(final);
            final.Result.Should().BeSameAs(cause);
            Record.Exception(scenario.Engine.Dispose).Should().BeSameAs(cause);
            scenario.Worker.HasExited.Should().BeTrue();
            scenario.VerifyRecovery();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FinalCloseRetainsLateWorkerFailureWhileWaitingForExit(bool afterPass)
        {
            using var scenario = new DirectCheckpointScenario();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var cause = new IOException("late worker failure");
            Action fail = () => { entered.Set(); AutomaticCheckpointWorker_Tests.Wait(release); throw cause; };
            if (afterPass) scenario.Worker.AfterCheckpointWorkerPass = fail;
            else scenario.Worker.BeforeCheckpointWorkerPass = fail;
            scenario.Worker.Request(scenario.Worker.Incarnation);
            AutomaticCheckpointWorker_Tests.Wait(entered);
            var closing = Start(scenario.Engine.Dispose);
            try
            {
                SpinWait.SpinUntil(() => scenario.State.Closing, TimeSpan.FromSeconds(5)).Should().BeTrue();
                scenario.State.Disposed.Should().BeFalse();
                closing.IsCompleted.Should().BeFalse();
            }
            finally { release.Set(); }
            await Completes(closing);
            closing.Result.Should().BeSameAs(cause);
            scenario.State.Disposed.Should().BeTrue();
            scenario.Worker.HasExited.Should().BeTrue();
            scenario.VerifyRecovery();
        }

        internal static Task<Exception> Start(Action action) => Task.Factory.StartNew(() => Record.Exception(action),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        internal static async Task Completes(Task task)
        {
            (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(task);
            await task;
        }
    }
}
