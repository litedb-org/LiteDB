using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2814TransactionIds_Tests
    {
        [Fact]
        public async Task Waiting_transaction_reserves_its_ID_only_after_checkpoint_admission()
        {
            using var engine = new LiteEngine();
            using var db = new LiteDatabase(engine);
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var locker = Field<LockService>(engine, "_locker");
            var wal = Field<WalIndexService>(engine, "_walIndex");
            var before = wal.LastTransactionID;
            using var waiting = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            locker.BeforeTransactionAdmission = () =>
            {
                waiting.Set();
                resume.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            };
            locker.EnterExclusive().Should().BeTrue();
            var transaction = Task.Run(() =>
            {
                var monitor = engine.GetMonitor();
                var created = monitor.GetTransaction(true, true, out _);
                try { return created.TransactionID; }
                finally { monitor.ReleaseTransaction(created); }
            });
            var reservedWhileWaiting = -1;
            try
            {
                waiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                reservedWhileWaiting = wal.LastTransactionID;
                engine.Checkpoint().Should().BeGreaterThan(0);
                wal.LastTransactionID.Should().Be(0);
            }
            finally
            {
                locker.BeforeTransactionAdmission = null;
                locker.ExitExclusive();
                resume.Set();
            }
            var id = await transaction;
            reservedWhileWaiting.Should().Be(before);
            id.Should().Be(1);
            db.GetCollection("rows").FindById(1)["_id"].AsInt32.Should().Be(1);
        }

        [Fact]
        public void Construction_failure_releases_the_admitted_read_lease()
        {
            using var engine = new LiteEngine();
            var locker = Field<LockService>(engine, "_locker");
            var wal = Field<WalIndexService>(engine, "_walIndex");
            // Failing reader construction occurs after ID reservation.
            using var monitor = new TransactionMonitor(null, locker, null, wal, 10);
            Action create = () => monitor.GetTransaction(true, true, out _);
            create.Should().Throw<NullReferenceException>();
            locker.TransactionsCount.Should().Be(0);
            monitor.Transactions.Should().BeEmpty();
            locker.TryEnterExclusive(out var mustExit).Should().BeTrue();
            if (mustExit) locker.ExitExclusive();
        }

        private static T Field<T>(LiteEngine engine, string name) =>
            (T)typeof(LiteEngine).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);
    }
}
