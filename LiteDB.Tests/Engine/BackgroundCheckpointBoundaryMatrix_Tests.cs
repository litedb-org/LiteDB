using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class BackgroundCheckpointBoundaryMatrix_Tests
    {
        public static IEnumerable<object[]> MaintenanceCases()
        {
            foreach (var boundary in BackgroundCheckpointBoundaryHold.Boundaries)
                foreach (var operation in new[] { "checkpoint", "rebuild", "close" })
                    yield return new object[] { boundary, operation };
        }

        public static IEnumerable<object[]> WriterCases()
        {
            foreach (var boundary in BackgroundCheckpointBoundaryHold.Boundaries)
                foreach (var operation in new[] { "commit", "rollback", "pragma" })
                    yield return new object[] { boundary, operation };
        }

        [Theory]
        [MemberData(nameof(MaintenanceCases))]
        public async Task MaintenanceJoinsWorkerAtEveryBoundary(string boundary, string operation)
        {
            using var scenario = new BackgroundCheckpointIntegrationScenario();
            var worker = scenario.Engine.InstallDirectCheckpointLifetime();
            using var hold = new BackgroundCheckpointBoundaryHold(scenario.Engine, worker, boundary);
            using var joining = new ManualResetEventSlim();
            worker.BeforeCheckpointWorkerPauseWait = joining.Set;
            for (var i = 1; i <= 3; i++) scenario.Database.UserVersion = i;
            AutomaticCheckpointWorker_Tests.Wait(hold.Entered);
            var count = -1;
            var action = DirectCheckpointLifetime_Tests.Start(() =>
            {
                if (operation == "close") scenario.Engine.Dispose();
                else if (operation == "rebuild") scenario.Engine.Rebuild();
                else count = scenario.Engine.Checkpoint();
            });
            try
            {
                if (operation == "close")
                {
                    var state = State(scenario.Engine);
                    SpinWait.SpinUntil(() => state.Closing, TimeSpan.FromSeconds(5)).Should().BeTrue();
                    state.Disposed.Should().BeFalse();
                }
                else AutomaticCheckpointWorker_Tests.Wait(joining);
                action.IsCompleted.Should().BeFalse("the coordinator boundary is still held");
            }
            finally
            {
                hold.Release.Set();
                await DirectCheckpointLifetime_Tests.Completes(action);
            }
            action.Result.Should().BeNull();
            hold.Hits.Should().Be(1);
            if (operation == "checkpoint") count.Should().Be(0, "the joined worker drained before the caller's own attempt");
            if (operation != "checkpoint") worker.HasExited.Should().BeTrue();
            scenario.VerifyRecovery(3);
        }

        [Theory]
        [MemberData(nameof(WriterCases))]
        public async Task WriterAndPragmaRespectWorkerStorageExclusion(string boundary, string operation)
        {
            using var scenario = new BackgroundCheckpointIntegrationScenario();
            var worker = scenario.Engine.InstallDirectCheckpointLifetime();
            using var hold = new BackgroundCheckpointBoundaryHold(scenario.Engine, worker, boundary);
            using var admission = new ManualResetEventSlim();
            var locker = (LockService)typeof(LiteEngine).GetField("_locker", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(scenario.Engine);
            for (var i = 1; i <= 3; i++) scenario.Database.UserVersion = i;
            AutomaticCheckpointWorker_Tests.Wait(hold.Entered);
            locker.BeforeTransactionAdmission = admission.Set;
            var writer = DirectCheckpointLifetime_Tests.Start(() =>
            {
                if (operation == "pragma") scenario.Database.CheckpointSize = 0;
                else
                {
                    scenario.Database.BeginTrans().Should().BeTrue();
                    scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(3));
                    if (operation == "commit") scenario.Database.Commit().Should().BeTrue();
                    else scenario.Database.Rollback().Should().BeTrue();
                }
            });
            try
            {
                AutomaticCheckpointWorker_Tests.Wait(admission);
                if (boundary == "before-pass" || boundary == "after-pass")
                {
                    await DirectCheckpointLifetime_Tests.Completes(writer);
                    writer.Result.Should().BeNull("a notification/completion hook owns no storage exclusion");
                }
                else writer.IsCompleted.Should().BeFalse("the pass owns transaction exclusion before this boundary");
            }
            finally
            {
                hold.Release.Set();
                await DirectCheckpointLifetime_Tests.Completes(writer);
                locker.BeforeTransactionAdmission = null;
            }
            writer.Result.Should().BeNull();
            scenario.Disk.WriteAdmission.Writers.Should().Be(0);
            scenario.Database.Checkpoint();
            scenario.Engine.Dispose();
            for (var open = 0; open < 3; open++)
            {
                using (var db = new LiteDatabase(scenario.File.Filename))
                {
                    db.UserVersion.Should().Be(3);
                    var expected = Enumerable.Range(0, operation == "commit" ? 4 : 3)
                        .Select(BackgroundCheckpointScenario.Row).ToArray();
                    db.GetCollection("rows").FindAll().Should().BeEquivalentTo(expected);
                    foreach (var id in Enumerable.Range(0, 5))
                        db.GetCollection("rows").Find(Query.EQ("value", id * 7)).Should()
                            .BeEquivalentTo(expected.Where(row => row["_id"].AsInt32 == id));
                    db.GetCollection("sentinel").FindById(1)["payload"].AsString.Should().Be("untouched");
                    db.Checkpoint();
                }
                AllocationReachability.Verify(scenario.File.Filename, null);
            }
        }

        internal static EngineState State(LiteEngine engine) => (EngineState)typeof(LiteEngine)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
    }
}
