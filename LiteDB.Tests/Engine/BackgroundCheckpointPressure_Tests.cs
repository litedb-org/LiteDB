using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Engine
{
    public class BackgroundCheckpointPressure_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("secret", false)]
        [InlineData("secret", true)]
        public async Task SustainedWritersWaitAndTimeoutAtHighWaterThenResumeAcrossThreeDrains(string password, bool durable)
        {
            using var scenario = new BackgroundCheckpointIntegrationScenario(password, durable);
            scenario.Database.Timeout = TimeSpan.FromSeconds(1);
            scenario.Database.Checkpoint();
            var worker = scenario.Engine.InstallDirectCheckpointLifetime();
            var starts = 0;
            worker.BeforeCheckpointWorkerStart = () => Interlocked.Increment(ref starts);
            for (var cycle = 0; cycle < 3; cycle++)
            {
                using var pass = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var waiting = new ManualResetEventSlim();
                var hits = 0;
                worker.BeforeCheckpointWorkerPass = () =>
                {
                    if (Interlocked.Increment(ref hits) != 1) return;
                    pass.Set();
                    AutomaticCheckpointWorker_Tests.Wait(release);
                };
                var baseline = cycle * 100;
                for (var i = 1; i <= 3; i++) scenario.Database.UserVersion = baseline + i;
                AutomaticCheckpointWorker_Tests.Wait(pass);
                Task<Exception> successor = null;
                try
                {
                    for (var i = 4; i <= 8; i++) scenario.Database.UserVersion = baseline + i;
                    var tail = scenario.Disk.GetFileLength(FileOrigin.Log);
                    tail.Should().Be(8L * PAGE_SIZE, "high water closes at equality");
                    scenario.Disk.WriteAdmission.UnderPressure.Should().BeTrue();
                    scenario.Disk.WriteAdmission.Writers.Should().Be(0);
                    scenario.Disk.WriteAdmission.BeforeCheckpointWriterWait = waiting.Set;
                    var timeout = DirectCheckpointLifetime_Tests.Start(() => scenario.Database.UserVersion = -1);
                    AutomaticCheckpointWorker_Tests.Wait(waiting);
                    await DirectCheckpointLifetime_Tests.Completes(timeout);
                    timeout.Result.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);
                    scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(tail);
                    scenario.Engine.GetMonitor().Transactions.Should().BeEmpty();
                    BackgroundCheckpointIntegrationScenario.Verify(scenario.Database, baseline + 8);
                    waiting.Reset();
                    successor = DirectCheckpointLifetime_Tests.Start(() =>
                    {
                        scenario.Database.BeginTrans().Should().BeTrue();
                        scenario.Disk.GetFileLength(FileOrigin.Log).Should().BeLessThanOrEqualTo(2L * PAGE_SIZE,
                            "a real checkpoint, rather than permit release or a timeout, must reopen this gate");
                        scenario.Database.GetCollection("rows").Update(BackgroundCheckpointScenario.Row(0)).Should().BeTrue();
                        scenario.Database.Commit().Should().BeTrue();
                        scenario.Database.UserVersion = baseline + 99;
                    });
                    AutomaticCheckpointWorker_Tests.Wait(waiting);
                    successor.IsCompleted.Should().BeFalse();
                }
                finally
                {
                    release.Set();
                    if (successor != null) await DirectCheckpointLifetime_Tests.Completes(successor);
                    scenario.Disk.WriteAdmission.BeforeCheckpointWriterWait = null;
                }
                successor.Result.Should().BeNull();
                scenario.Database.Checkpoint();
                scenario.Disk.WriteAdmission.UnderPressure.Should().BeFalse();
                scenario.Disk.WriteAdmission.Writers.Should().Be(0);
                scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(0);
                starts.Should().Be(1, "all cycles reuse the single process-owned worker");
            }
            scenario.VerifyRecovery(299);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OversizeOwnerCanFinishWhileSuccessorWaitsAndWorkerMakesPinnedAttempts(bool rollback)
        {
            using var scenario = new BackgroundCheckpointIntegrationScenario(transactionPages: 2);
            scenario.Database.Timeout = TimeSpan.FromSeconds(1);
            scenario.Database.Checkpoint();
            var worker = scenario.Engine.InstallDirectCheckpointLifetime();
            using var attempted = new ManualResetEventSlim();
            worker.AfterCheckpointWorkerPass = attempted.Set;
            scenario.Database.BeginTrans().Should().BeTrue();
            var extra = Enumerable.Range(3, 90).Select(BackgroundCheckpointScenario.Row).ToArray();
            scenario.Database.GetCollection("rows").Insert(extra);
            var high = scenario.Disk.GetFileLength(FileOrigin.Log);
            high.Should().BeGreaterThan(8L * PAGE_SIZE);
            scenario.Disk.WriteAdmission.Writers.Should().Be(1);
            // This intentionally requests a pass before owner completion. It must
            // preserve the unconfirmed safepoints and cannot wait on its permit.
            worker.Request(worker.Incarnation);
            AutomaticCheckpointWorker_Tests.Wait(attempted);
            scenario.Disk.WriteAdmission.UnderPressure.Should().BeTrue();
            var successor = DirectCheckpointLifetime_Tests.Start(() => scenario.Database.UserVersion = -1);
            DirectCheckpointLifetime_Tests.Completes(successor).GetAwaiter().GetResult();
            successor.Result.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);
            if (rollback) scenario.Database.Rollback().Should().BeTrue();
            else scenario.Database.Commit().Should().BeTrue();
            scenario.Disk.WriteAdmission.Writers.Should().Be(0);
            scenario.Database.Checkpoint();
            scenario.Disk.WriteAdmission.UnderPressure.Should().BeFalse();
            scenario.Engine.Dispose();
            for (var open = 0; open < 3; open++)
            {
                using (var db = new LiteDatabase(scenario.File.Filename))
                {
                    var expected = Enumerable.Range(0, rollback ? 3 : 93).Select(BackgroundCheckpointScenario.Row).ToArray();
                    db.GetCollection("rows").FindAll().Should().BeEquivalentTo(expected);
                    foreach (var id in Enumerable.Range(0, 94))
                        db.GetCollection("rows").Find(Query.EQ("value", id * 7)).Should()
                            .BeEquivalentTo(expected.Where(row => row["_id"].AsInt32 == id));
                    db.GetCollection("sentinel").FindById(1)["payload"].AsString.Should().Be("untouched");
                    db.Checkpoint();
                }
                AllocationReachability.Verify(scenario.File.Filename, null);
            }
        }
    }
}
