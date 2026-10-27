using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class AutomaticCheckpointWorkerFailure_Tests
    {
        [Theory]
        [InlineData("start")]
        [InlineData("before-pass")]
        [InlineData("after-pass")]
        [InlineData("data-write")]
        public void WorkerFailureIsTerminalForAllFacadesAndAttachment(string boundary)
        {
            using var scenario = new BackgroundCheckpointScenario();
            using var peer = new LiteDatabase(scenario.File.Filename);
            scenario.Disk.WriteAdmission = new CheckpointWriteAdmission(1, 0);
            var permits = new CheckpointWriteAdmission.Permit[64];
            for (var i = 0; i < permits.Length; i++)
                permits[i] = scenario.Disk.WriteAdmission.Acquire(TimeSpan.Zero, false, () => { });
            using var waiting = new ManualResetEventSlim();
            scenario.Disk.WriteAdmission.BeforeCheckpointWriterWait = waiting.Set;
            var blocked = Task.Run(() => Record.Exception(() => peer.BeginTrans()));
            AutomaticCheckpointWorker_Tests.Wait(waiting);
            var worker = AutomaticCheckpointWorker_Tests.Install(scenario);
            var cause = new IOException("worker fault: " + boundary);
            Action fail = () => throw cause;
            if (boundary == "start") worker.BeforeCheckpointWorkerStart = fail;
            if (boundary == "before-pass") worker.BeforeCheckpointWorkerPass = fail;
            if (boundary == "after-pass") worker.AfterCheckpointWorkerPass = fail;
            if (boundary == "data-write") scenario.State.SimulateDataWriteFail = _ => throw cause;
            try
            {
                var error = Record.Exception(() => worker.Request(worker.Incarnation));
                if (boundary == "start") error.Should().BeSameAs(cause);
                else error.Should().BeNull();
                SpinWait.SpinUntil(() => worker.State == AutomaticCheckpointState.Faulted,
                    TimeSpan.FromSeconds(5)).Should().BeTrue();
                Record.Exception(worker.Dispose).Should().BeSameAs(cause);
                var admissionFailure = blocked.GetAwaiter().GetResult();
                admissionFailure.Should().BeOfType<IOException>();
                while (admissionFailure.InnerException != null) admissionFailure = admissionFailure.InnerException;
                admissionFailure.Should().BeSameAs(cause);
                foreach (var permit in permits) permit.Dispose();
                scenario.Disk.WriteAdmission.Writers.Should().Be(0);
                Action read = () => peer.GetCollection("rows").FindById(0);
                Action write = () => scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(1));
                Action checkpoint = () => scenario.Wal.Checkpoint();
                Action attach = () => { using var refused = new LiteDatabase(scenario.File.Filename); };
                read.Should().Throw<IOException>().Which.InnerException.Should().BeSameAs(cause);
                write.Should().Throw<IOException>().Which.InnerException.Should().BeSameAs(cause);
                checkpoint.Should().Throw<IOException>();
                attach.Should().Throw<IOException>();
                worker.Request(worker.Incarnation);
                worker.ReadersChanged();
                worker.Configure(0, 0);
                Record.Exception(worker.Dispose).Should().BeSameAs(cause);
            }
            finally
            {
                Record.Exception(worker.Dispose);
                foreach (var permit in permits) permit.Dispose();
                scenario.Disk.AutomaticCheckpoint = null;
            }
            peer.Dispose();
            scenario.VerifyRecovery(false);
        }

        [Theory]
        [InlineData("data-page")]
        [InlineData("data-flushed")]
        public async Task ComponentCloseJoinsPersistentPassBeforeReturning(string stage)
        {
            using var scenario = new BackgroundCheckpointScenario();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var worker = AutomaticCheckpointWorker_Tests.Install(scenario);
            var hit = 0;
            scenario.Engine.CheckpointStage = current =>
            {
                if (current != stage) return;
                Interlocked.Increment(ref hit);
                entered.Set();
                AutomaticCheckpointWorker_Tests.Wait(release);
            };
            worker.Request(worker.Incarnation);
            Task closing = null;
            try
            {
                AutomaticCheckpointWorker_Tests.Wait(entered);
                closing = Task.Run(worker.Dispose);
                SpinWait.SpinUntil(() => worker.State == AutomaticCheckpointState.Closing,
                    TimeSpan.FromSeconds(5)).Should().BeTrue();
                closing.IsCompleted.Should().BeFalse();
            }
            finally { release.Set(); }
            await closing;
            hit.Should().Be(1);
            worker.State.Should().Be(AutomaticCheckpointState.Stopped);
            scenario.Disk.AutomaticCheckpoint = null;
            scenario.VerifyRecovery(false);
        }

        [Fact]
        public void WorkerCanRequestItsOwnExitAndExternalCloseStillJoins()
        {
            using var scenario = new BackgroundCheckpointScenario();
            using var exited = new ManualResetEventSlim();
            using var worker = AutomaticCheckpointWorker_Tests.Install(scenario);
            worker.AfterCheckpointWorkerPass = () => { worker.Dispose(); exited.Set(); };
            worker.Request(worker.Incarnation);
            AutomaticCheckpointWorker_Tests.Wait(exited);
            worker.Dispose();
            worker.State.Should().Be(AutomaticCheckpointState.Stopped);
        }
    }
}
