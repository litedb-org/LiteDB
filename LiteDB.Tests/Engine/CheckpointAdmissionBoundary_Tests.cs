using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CheckpointAdmissionBoundary_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OversizeSafepointsCloseFreshAdmissionButOwnerCanFinish(bool commit)
        {
            using var scenario = new CheckpointAdmissionScenario();
            var db = scenario.Database;
            db.BeginTrans();
            db.GetCollection("rows").Insert(CheckpointAdmissionScenario.Row(1));
            scenario.Monitor.GetThreadTransaction().Safepoint();
            scenario.Disk.GetFileLength(FileOrigin.Log).Should().BeGreaterThanOrEqualTo(4L * Constants.PAGE_SIZE);
            var admission = scenario.Disk.WriteAdmission;
            admission.UnderPressure.Should().BeTrue();
            admission.Writers.Should().Be(1);
            var enumerated = false;
            IEnumerable<BsonDocument> Input()
            {
                enumerated = true;
                yield return new BsonDocument { ["_id"] = 9 };
            }
            var error = Task.Run(() => Record.Exception(() => db.GetCollection("other").Insert(Input()))).GetAwaiter().GetResult();
            error.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);
            enumerated.Should().BeFalse();
            scenario.Monitor.Transactions.Should().HaveCount(1);
            // A pure reader is still admitted, and reads its acknowledged predecessor.
            ((object)Task.Run(() => db.GetCollection("rows").FindById(0)).GetAwaiter().GetResult()).Should().BeEquivalentTo(CheckpointAdmissionScenario.Row(0));
            if (commit) db.Commit();
            else
            {
                var before = scenario.Disk.GetFileLength(FileOrigin.Log);
                db.Rollback();
                scenario.Disk.GetFileLength(FileOrigin.Log).Should().BeGreaterThan(before, "allocation rollback appends under the same permit");
                admission.UnderPressure.Should().BeTrue();
                db.Checkpoint();
            }
            admission.Writers.Should().Be(0);
            admission.UnderPressure.Should().BeFalse();
            scenario.VerifyRecovery(commit);
        }

        [Fact]
        public void ReadUpgradeRefusesWithoutWaitingOrChangingTheOuterTransaction()
        {
            using var scenario = new CheckpointAdmissionScenario();
            var transaction = scenario.Monitor.GetTransaction(true, false, out _, write: false);
            var snapshot = transaction.CreateSnapshot(LockMode.Read, "rows", false);
            scenario.Disk.WriteAdmission.PublishTail(4L * Constants.PAGE_SIZE);
            scenario.Disk.WriteAdmission.BeforeCheckpointWriterWait = () => throw new Exception("upgrade waited");
            Action upgrade = () => scenario.Monitor.GetTransaction(true, false, out _, write: true);
            upgrade.Should().Throw<LiteException>().Which.Message.Should().Contain("Complete the current reader");
            transaction.State.Should().Be(TransactionState.Active);
            transaction.CreateSnapshot(LockMode.Read, "rows", false).Should().BeSameAs(snapshot);
            transaction.Pages.HeaderChanged.Should().BeFalse();
            scenario.Disk.WriteAdmission.Writers.Should().Be(0);
            scenario.Disk.WriteAdmission.PublishTail(0);
            scenario.Monitor.GetTransaction(true, false, out _, write: true).Should().BeSameAs(transaction);
            scenario.Disk.WriteAdmission.Writers.Should().Be(1);
            scenario.Engine.Rollback();
            scenario.Disk.WriteAdmission.Writers.Should().Be(0);
            scenario.VerifyRecovery(false);
        }

        [Fact]
        public void ReentrantReadCallbackCannotWaitForItsOwnReader()
        {
            using var scenario = new CheckpointAdmissionScenario();
            using var reader = scenario.Engine.Query("rows", new Query());
            reader.Read().Should().BeTrue();
            scenario.Disk.WriteAdmission.PublishTail(4L * Constants.PAGE_SIZE);
            scenario.Disk.WriteAdmission.BeforeCheckpointWriterWait = () => throw new Exception("callback waited");
            Action write = () => scenario.Database.GetCollection("rows").Insert(CheckpointAdmissionScenario.Row(1));
            write.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);
            ((object)reader.Current.AsDocument).Should().BeEquivalentTo(CheckpointAdmissionScenario.Row(0));
            reader.Dispose();
            scenario.Monitor.Transactions.Should().BeEmpty();
            scenario.Disk.WriteAdmission.Writers.Should().Be(0);
            scenario.VerifyRecovery(false);
        }

        [Fact]
        public async Task RegistrationDisposeRaceReturnsProvisionalPermit()
        {
            using var scenario = new CheckpointAdmissionScenario();
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            var locker = scenario.Field<LockService>("_locker");
            locker.BeforeTransactionAdmission = () =>
            {
                entered.Set();
                resume.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            };
            var task = Task.Run(() => Record.Exception(() => scenario.Engine.BeginTrans()));
            try
            {
                entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                scenario.Disk.WriteAdmission.Writers.Should().Be(1);
                scenario.Monitor.DisposeOwner();
            }
            finally { resume.Set(); }
            (await task).Should().BeOfType<ObjectDisposedException>();
            scenario.Disk.WriteAdmission.Writers.Should().Be(0);
            scenario.Monitor.Transactions.Should().BeEmpty();
        }

        [Fact]
        public async Task ReadUpgradeDisposeRaceReturnsUnregisteredPermit()
        {
            using var scenario = new CheckpointAdmissionScenario();
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            scenario.Monitor.BeforeCheckpointPermitRegistration = () =>
            {
                entered.Set();
                resume.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            };
            var task = Task.Run(() =>
            {
                var transaction = scenario.Monitor.GetTransaction(true, false, out _, write: false);
                transaction.CreateSnapshot(LockMode.Read, "rows", false);
                return Record.Exception(() => scenario.Monitor.GetTransaction(true, false, out _, write: true));
            });
            try
            {
                entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                scenario.Disk.WriteAdmission.Writers.Should().Be(1);
                scenario.Monitor.DisposeOwner();
            }
            finally { resume.Set(); }
            (await task).Should().BeOfType<ObjectDisposedException>();
            scenario.Disk.WriteAdmission.Writers.Should().Be(0);
            scenario.Monitor.Transactions.Should().BeEmpty();
        }

        [Fact]
        public void ForeignFacadeIsRejectedBeforeAdmissionWait()
        {
            using var scenario = new CheckpointAdmissionScenario();
            using var peerEngine = new LiteEngine(new EngineSettings { Filename = scenario.File.Filename, TransactionPageLimit = 8 });
            using var peer = new LiteDatabase(peerEngine, disposeOnClose: false);
            scenario.Database.BeginTrans();
            scenario.Disk.WriteAdmission.PublishTail(4L * Constants.PAGE_SIZE);
            scenario.Disk.WriteAdmission.BeforeCheckpointWriterWait = () => throw new Exception("foreign owner waited");
            Action write = () => peer.GetCollection("rows").Insert(CheckpointAdmissionScenario.Row(1));
            write.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Another Direct facade");
            scenario.Disk.WriteAdmission.Writers.Should().Be(1);
            scenario.Database.Rollback();
            scenario.Disk.WriteAdmission.Writers.Should().Be(0);
        }

        [Fact]
        public void CommittedPragmasReconfigureWithoutResettingPermitIdentities()
        {
            using var scenario = new CheckpointAdmissionScenario(0);
            var admission = scenario.Disk.WriteAdmission;
            var inherited = Enumerable.Range(0, 70).Select(_ => admission.Acquire(TimeSpan.Zero, false, () => { })).ToArray();
            try
            {
                scenario.Database.CheckpointSize = 1;
                admission.Writers.Should().Be(70);
                Action blocked = () => scenario.Engine.BeginTrans();
                blocked.Should().Throw<LiteException>();
                foreach (var permit in inherited) permit.Dispose();
                scenario.Database.CheckpointSize = 0;
                scenario.Database.BeginTrans().Should().BeTrue();
                scenario.Database.Rollback();
                admission.Writers.Should().Be(0);
            }
            finally { foreach (var permit in inherited) permit.Dispose(); }
            scenario.VerifyRecovery(false);
        }
    }
}
