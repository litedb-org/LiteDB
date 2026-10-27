#if NET8_0_OR_GREATER
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
    public class BackgroundCheckpointGroupLifetime_Tests
    {
        [Theory]
        [InlineData(false, 1000)]
        [InlineData(true, 1000)]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        public async Task FinalCloseWaitsForCapturedGroupSyncAndRetainsRealLateErrors(bool syncFails, int checkpoint)
        {
            using var scenario = new DirectCheckpointScenario(checkpoint);
            using var captured = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var followerWaiting = new ManualResetEventSlim();
            var groupWaits = 0;
            var syncs = 0;
            var groupSyncs = 0;
            scenario.Engine.SimulateCrashPoint = phase =>
            {
                if (phase == "wal-before-durable-flush") Interlocked.Increment(ref groupSyncs);
                if (phase == "wal-before-group-wait" && Interlocked.Increment(ref groupWaits) == 2) followerWaiting.Set();
            };
            NativeFileSync.SimulateErrno = path =>
            {
                if (path != NativeFileSyncCollection.LogPath(scenario.File.Filename)) return 0;
                if (Interlocked.Increment(ref syncs) == 1)
                {
                    captured.Set();
                    AutomaticCheckpointWorker_Tests.Wait(release);
                    return syncFails ? 5 : 0;
                }
                return 0;
            };
            var writer = DirectCheckpointLifetime_Tests.Start(() => scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(2)));
            Task<Exception> closing = null;
            Task<Exception> follower = null;
            try
            {
                AutomaticCheckpointWorker_Tests.Wait(captured);
                var logicalBytes = scenario.Disk.GetFileLength(FileOrigin.Log);
                follower = DirectCheckpointLifetime_Tests.Start(() =>
                {
                    scenario.Database.BeginTrans().Should().BeTrue();
                    scenario.Database.GetCollection("rows").Delete(99).Should().BeFalse();
                    scenario.Database.Commit().Should().BeTrue();
                });
                AutomaticCheckpointWorker_Tests.Wait(followerWaiting);
                scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(logicalBytes, "the follower appended zero frames");
                scenario.Worker.Request(scenario.Worker.Incarnation);
                closing = DirectCheckpointLifetime_Tests.Start(scenario.Engine.Dispose);
                SpinWait.SpinUntil(() => scenario.State.Closing, TimeSpan.FromSeconds(5)).Should().BeTrue();
                scenario.State.Disposed.Should().BeFalse();
                closing.IsCompleted.Should().BeFalse();
                // Final close cannot retire the captured descriptor to interrupt sync.
                writer.IsCompleted.Should().BeFalse();
            }
            finally { release.Set(); }
            try
            {
                await DirectCheckpointLifetime_Tests.Completes(writer);
                await DirectCheckpointLifetime_Tests.Completes(follower);
                await DirectCheckpointLifetime_Tests.Completes(closing);
                follower.Result.Should().BeAssignableTo<IOException>();
                writer.Result.Should().BeAssignableTo<IOException>();
                if (syncFails)
                {
                    scenario.State.Stopped.Should().BeTrue();
                    closing.Result.Should().BeSameAs(scenario.State.TerminalCause);
                    closing.Result.Should().BeAssignableTo<IOException>();
                }
                else
                {
                    closing.Result.Should().BeNull();
                    scenario.State.Stopped.Should().BeFalse("deliberately refusing a pending ticket is not a device failure");
                }
                groupSyncs.Should().Be(1);
                if (syncFails || checkpoint == 0) syncs.Should().Be(1, "a failed group or CP0 forbids subsequent checkpoint syncs");
            }
            finally { NativeFileSync.SimulateErrno = null; }
            scenario.VerifyRecovery(uncertain: true);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task CompetingWorkerAndGroupFailuresRetainOneCauseForEveryFacade(bool workerWins)
        {
            using var scenario = new DirectCheckpointScenario();
            var peer = new LiteEngine(scenario.File.Filename);
            using var groupSync = new ManualResetEventSlim();
            using var releaseGroup = new ManualResetEventSlim();
            using var workerPass = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            var workerCause = new IOException("first worker failure");
            var syncs = 0;
            var dataWrites = 0;
            scenario.State.SimulateDataWriteFail = _ => Interlocked.Increment(ref dataWrites);
            scenario.Worker.BeforeCheckpointWorkerPass = () =>
            {
                workerPass.Set();
                AutomaticCheckpointWorker_Tests.Wait(releaseWorker);
                if (workerWins) throw workerCause;
            };
            NativeFileSync.SimulateErrno = path =>
            {
                if (path != NativeFileSyncCollection.LogPath(scenario.File.Filename)) return 0;
                Interlocked.Increment(ref syncs);
                groupSync.Set();
                AutomaticCheckpointWorker_Tests.Wait(releaseGroup);
                return 5;
            };
            var writer = DirectCheckpointLifetime_Tests.Start(() => scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(2)));
            Task<Exception> closing = null;
            try
            {
                AutomaticCheckpointWorker_Tests.Wait(groupSync);
                scenario.Worker.Request(scenario.Worker.Incarnation);
                AutomaticCheckpointWorker_Tests.Wait(workerPass);
                if (workerWins) releaseWorker.Set();
                else releaseGroup.Set();
                SpinWait.SpinUntil(() => scenario.State.Stopped, TimeSpan.FromSeconds(5)).Should().BeTrue();
                var cause = scenario.State.TerminalCause;
                if (workerWins) cause.Should().BeSameAs(workerCause);
                else cause.Should().BeOfType<FileSyncException>();
                Record.Exception(() => peer.BeginTrans()).Should().BeOfType<IOException>().Which.InnerException.Should().BeSameAs(cause);
                Record.Exception(peer.Dispose).Should().BeSameAs(cause);
                closing = DirectCheckpointLifetime_Tests.Start(scenario.Engine.Dispose);
                closing.IsCompleted.Should().BeFalse();
                releaseWorker.Set();
                releaseGroup.Set();
                await DirectCheckpointLifetime_Tests.Completes(writer);
                await DirectCheckpointLifetime_Tests.Completes(closing);
                writer.Result.Should().BeAssignableTo<IOException>();
                closing.Result.Should().BeSameAs(cause);
                scenario.State.TerminalCause.Should().BeSameAs(cause);
                dataWrites.Should().Be(0, "a stopped worker must not start a checkpoint write");
                syncs.Should().Be(1);
            }
            finally
            {
                releaseWorker.Set();
                releaseGroup.Set();
                await DirectCheckpointLifetime_Tests.Completes(writer);
                if (closing != null) await DirectCheckpointLifetime_Tests.Completes(closing);
                NativeFileSync.SimulateErrno = null;
                Record.Exception(peer.Dispose);
            }
            scenario.VerifyRecovery(uncertain: true);
        }
    }
}
#endif
