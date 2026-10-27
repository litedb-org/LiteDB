using System;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Engine
{
    public class BackgroundCheckpointRouting_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("secret", false)]
        [InlineData("secret", true)]
        public void FirstExcessPageQueuesAfterReleaseAndExplicitCheckpointCompletes(string password, bool durable)
        {
            using var scenario = new BackgroundCheckpointIntegrationScenario(password, durable);
            var worker = scenario.Engine.InstallDirectCheckpointLifetime();
            var starts = 0;
            worker.BeforeCheckpointWorkerStart = () => starts++;
            var pause = worker.Pause();
            try
            {
                for (var value = 1; value <= 2; value++)
                {
                    scenario.Database.UserVersion = value;
                    scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be((long)value * PAGE_SIZE);
                    worker.RequestEpoch.Should().Be(0, "equality and checksum padding do not request maintenance");
                }
                scenario.Database.UserVersion = 3;
                scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(3L * PAGE_SIZE);
                scenario.Disk.WriteAdmission.Writers.Should().Be(0);
                scenario.Engine.GetMonitor().Transactions.Should().BeEmpty();
                worker.RequestEpoch.Should().BeGreaterThan(0);
                starts.Should().Be(1);
                BackgroundCheckpointIntegrationScenario.Verify(scenario.Database, 3);
                using var peer = new LiteEngine(new EngineSettings
                {
                    Filename = scenario.File.Filename, Password = password, DurableCommits = durable
                });
                peer.InstallDirectCheckpointLifetime().Should().BeSameAs(worker);
                Record.Exception(() => peer.Checkpoint()).Should().BeOfType<InvalidOperationException>();
            }
            finally { pause.Dispose(); }
            scenario.Database.Checkpoint();
            scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(0);
            scenario.VerifyRecovery(3);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExplicitTransactionAndRollbackRequestOnlyAfterTheirOwnerReleases(bool rollback)
        {
            using var scenario = new BackgroundCheckpointIntegrationScenario(transactionPages: 2);
            var worker = scenario.Engine.InstallDirectCheckpointLifetime();
            using (worker.Pause())
            {
                scenario.Database.BeginTrans().Should().BeTrue();
                scenario.Database.GetCollection("rows").Insert(Enumerable.Range(3, 60).Select(BackgroundCheckpointScenario.Row));
                scenario.Disk.GetFileLength(FileOrigin.Log).Should().BeGreaterThan(8L * PAGE_SIZE,
                    "safepoints close admission without starting the lazy worker under the transaction");
                scenario.Disk.WriteAdmission.UnderPressure.Should().BeTrue();
                var starts = 0;
                worker.BeforeCheckpointWorkerStart = () =>
                {
                    scenario.Disk.WriteAdmission.Writers.Should().Be(0);
                    scenario.Engine.GetMonitor().Transactions.Should().BeEmpty();
                    starts++;
                };
                if (rollback) scenario.Database.Rollback().Should().BeTrue();
                else scenario.Database.Commit().Should().BeTrue();
                starts.Should().Be(1);
                worker.RequestEpoch.Should().BeGreaterThan(0);
            }
            scenario.Database.Checkpoint();
            scenario.Disk.WriteAdmission.UnderPressure.Should().BeFalse();
            if (!rollback)
            {
                scenario.Database.GetCollection("rows").FindAll().Should().BeEquivalentTo(
                    Enumerable.Range(0, 63).Select(BackgroundCheckpointScenario.Row));
                foreach (var id in Enumerable.Range(0, 63))
                    scenario.Database.GetCollection("rows").Find(Query.EQ("value", id * 7)).Should()
                        .BeEquivalentTo(new[] { BackgroundCheckpointScenario.Row(id) });
                // Restore the independent baseline after proving the full commit.
                scenario.Database.GetCollection("rows").DeleteMany("_id >= 3").Should().Be(60);
            }
            scenario.Database.GetCollection("rows").Find(Query.GTE("value", 21)).Should().BeEmpty();
            scenario.VerifyRecovery(0);
        }

        [Fact]
        public void CommittedZeroCancelsQueuedWorkAndReenableStartsFromCurrentTail()
        {
            using var scenario = new BackgroundCheckpointIntegrationScenario();
            var worker = scenario.Engine.InstallDirectCheckpointLifetime();
            var passes = 0;
            worker.BeforeCheckpointWorkerPass = () => Interlocked.Increment(ref passes);
            using (worker.Pause())
            {
                for (var value = 1; value <= 3; value++) scenario.Database.UserVersion = value;
                scenario.Database.CheckpointSize = 0;
                scenario.Disk.WriteAdmission.UnderPressure.Should().BeFalse();
            }
            worker.State.Should().Be(AutomaticCheckpointState.Idle);
            passes.Should().Be(0);
            scenario.Database.UserVersion = 4;
            using (worker.Pause())
            {
                scenario.Database.CheckpointSize = 1;
                scenario.Disk.WriteAdmission.UnderPressure.Should().BeTrue();
                scenario.Disk.WriteAdmission.Writers.Should().Be(0);
            }
            scenario.Database.Checkpoint();
            scenario.Disk.WriteAdmission.UnderPressure.Should().BeFalse();
            scenario.VerifyRecovery(4);
        }

        [Fact]
        public void MaximumPositivePragmaDoesNotOverflowOrStartWorker()
        {
            using var scenario = new BackgroundCheckpointIntegrationScenario(checkpoint: int.MaxValue);
            var worker = scenario.Engine.InstallDirectCheckpointLifetime();
            for (var value = 1; value <= 3; value++) scenario.Database.UserVersion = value;
            worker.RequestEpoch.Should().Be(0);
            scenario.Disk.WriteAdmission.UnderPressure.Should().BeFalse();
            scenario.Disk.GetFileLength(FileOrigin.Log).Should().Be(3L * PAGE_SIZE);
            scenario.VerifyRecovery(3);
        }
    }
}
