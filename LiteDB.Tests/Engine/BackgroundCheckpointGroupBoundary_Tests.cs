#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection(NativeFileSyncCollection.Name)]
    public class BackgroundCheckpointGroupBoundary_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task WorkerAndCapturedLeaderOrderZeroFrameFollowerAndFailure(bool workerFirst, bool fail)
        {
            using var scenario = new DirectCheckpointScenario();
            using var leaderReady = new ManualResetEventSlim();
            using var followerReady = new ManualResetEventSlim();
            using var releaseGroup = new ManualResetEventSlim();
            using var workerReady = new ManualResetEventSlim();
            using var releaseWorker = new ManualResetEventSlim();
            using var completedPass = new ManualResetEventSlim();
            var groupEntrants = 0;
            var syncs = 0;
            var dataWrites = 0;
            var cause = new IOException("background pass failed before index ownership");
            scenario.Engine.SimulateDataWriteFail = _ => Interlocked.Increment(ref dataWrites);
            scenario.Engine.SimulateCrashPoint = phase =>
            {
                if (!workerFirst || phase != "wal-before-group-wait") return;
                if (Interlocked.Increment(ref groupEntrants) == 1) leaderReady.Set();
                else followerReady.Set();
                AutomaticCheckpointWorker_Tests.Wait(releaseGroup);
            };
            scenario.Engine.CheckpointStage = phase =>
            {
                if (phase != (workerFirst ? "before-index-lock" : "before-barrier-lock")) return;
                workerReady.Set();
                if (!workerFirst) return;
                AutomaticCheckpointWorker_Tests.Wait(releaseWorker);
                if (fail) throw cause;
            };
            scenario.Worker.AfterCheckpointWorkerPass = () =>
            {
                completedPass.Set();
                AutomaticCheckpointWorker_Tests.Wait(releaseWorker);
            };
            NativeFileSync.SimulateErrno = path =>
            {
                if (scenario.Worker.IsWorkerThread || path != NativeFileSyncCollection.LogPath(scenario.File.Filename)) return 0;
                Interlocked.Increment(ref syncs);
                if (!workerFirst)
                {
                    leaderReady.Set();
                    AutomaticCheckpointWorker_Tests.Wait(releaseGroup);
                }
                return !workerFirst && fail ? 5 : 0;
            };
            Task<Exception> writer = null, follower = null;
            try
            {
                writer = DirectCheckpointLifetime_Tests.Start(() =>
                    scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(2)));
                AutomaticCheckpointWorker_Tests.Wait(leaderReady);
                follower = DirectCheckpointLifetime_Tests.Start(() =>
                {
                    scenario.Database.BeginTrans().Should().BeTrue();
                    scenario.Database.GetCollection("rows").Delete(99).Should().BeFalse();
                    var transaction = scenario.Engine.GetMonitor().GetThreadTransaction();
                    transaction.Pages.CommitTicket.Should().BeGreaterThan(0, "the zero-frame transaction depends on the captured leader");
                    if (!workerFirst) followerReady.Set();
                    scenario.Database.Commit().Should().BeTrue();
                });
                AutomaticCheckpointWorker_Tests.Wait(followerReady);
                scenario.Worker.Request(scenario.Worker.Incarnation);
                AutomaticCheckpointWorker_Tests.Wait(workerReady);
                writer.IsCompleted.Should().BeFalse();
                follower.IsCompleted.Should().BeFalse();
                if (workerFirst)
                {
                    syncs.Should().Be(0);
                    releaseWorker.Set();
                    if (fail)
                        SpinWait.SpinUntil(() => scenario.State.Stopped, TimeSpan.FromSeconds(5)).Should().BeTrue();
                    else
                    {
                        AutomaticCheckpointWorker_Tests.Wait(completedPass);
                        scenario.Disk.GetFileLength(FileOrigin.Log).Should().BeGreaterThan(0,
                            "a worker cannot reclaim a pending generation or acknowledge its tickets");
                    }
                }
                else dataWrites.Should().Be(0, "native leader sync owns the checkpoint barrier");
                releaseGroup.Set();
                await DirectCheckpointLifetime_Tests.Completes(writer);
                await DirectCheckpointLifetime_Tests.Completes(follower);
                if (fail)
                {
                    writer.Result.Should().BeAssignableTo<IOException>();
                    follower.Result.Should().BeAssignableTo<IOException>();
                    scenario.State.Stopped.Should().BeTrue();
                    if (workerFirst) scenario.State.TerminalCause.Should().BeSameAs(cause);
                    dataWrites.Should().Be(0);
                    syncs.Should().Be(workerFirst ? 0 : 1);
                }
                else
                {
                    writer.Result.Should().BeNull();
                    follower.Result.Should().BeNull();
                    releaseWorker.Set();
                    scenario.Engine.CheckpointStage = null;
                    scenario.Database.Checkpoint();
                    scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(0);
                }
            }
            finally
            {
                releaseWorker.Set();
                releaseGroup.Set();
                if (writer != null) await DirectCheckpointLifetime_Tests.Completes(writer);
                if (follower != null) await DirectCheckpointLifetime_Tests.Completes(follower);
                Record.Exception(scenario.Engine.Dispose);
                NativeFileSync.SimulateErrno = null;
            }
            for (var open = 0; open < 3; open++)
            {
                using (var db = new LiteDatabase(scenario.File.Filename))
                {
                    var rows = db.GetCollection("rows");
                    var count = fail && rows.FindById(2) == null ? 2 : 3;
                    var expected = Enumerable.Range(0, count).Select(BackgroundCheckpointScenario.Row).ToArray();
                    rows.FindAll().Should().BeEquivalentTo(expected);
                    foreach (var id in Enumerable.Range(0, 4))
                        rows.Find(Query.EQ("value", id * 7)).Should()
                            .BeEquivalentTo(expected.Where(row => row["_id"].AsInt32 == id));
                    db.GetCollection("sentinel").FindById(1)["payload"].AsString.Should().Be("untouched");
                    db.Checkpoint();
                }
                AllocationReachability.Verify(scenario.File.Filename, null);
            }
        }
    }
}
#endif
