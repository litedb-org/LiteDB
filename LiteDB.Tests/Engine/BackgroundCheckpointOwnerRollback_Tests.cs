using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class BackgroundCheckpointOwnerRollback_Tests
    {
        [Fact]
        public void NonFinalOwnerRollbackNotifiesAfterReleasingSnapshotsAndPermit()
        {
            using var scenario = new BackgroundCheckpointIntegrationScenario(transactionPages: 2);
            var worker = scenario.Engine.InstallDirectCheckpointLifetime();
            using var peer = new LiteEngine(new EngineSettings
            {
                Filename = scenario.File.Filename, DurableCommits = false, TransactionPageLimit = 2
            });
            using var db = new LiteDatabase(peer, disposeOnClose: false);
            var starts = 0;
            worker.BeforeCheckpointWorkerStart = () =>
            {
                peer.GetMonitor().Transactions.Should().BeEmpty();
                scenario.Disk.WriteAdmission.Writers.Should().Be(0);
                starts++;
            };
            using (worker.Pause())
            {
                scenario.Database.BeginTrans().Should().BeTrue();
                scenario.Database.GetCollection("rows").Insert(Enumerable.Range(3, 60).Select(BackgroundCheckpointScenario.Row));
                scenario.Disk.GetFileLength(FileOrigin.Log).Should().BeGreaterThan(8L * Constants.PAGE_SIZE);
                scenario.Disk.WriteAdmission.UnderPressure.Should().BeTrue();
                starts.Should().Be(0);
                scenario.Engine.Dispose();
                starts.Should().Be(1, "non-final owner rollback must not strand the lazy worker above high water");
                worker.HasExited.Should().BeFalse();
                BackgroundCheckpointIntegrationScenario.Verify(db, 0);
                db.GetCollection("rows").Find(Query.GTE("value", 21)).Should().BeEmpty();
            }
            db.Checkpoint();
            scenario.Disk.WriteAdmission.UnderPressure.Should().BeFalse();
            db.UserVersion = 1;
            peer.Dispose();
            scenario.VerifyRecovery(1);
        }
    }
}
