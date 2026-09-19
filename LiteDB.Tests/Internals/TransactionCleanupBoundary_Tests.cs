using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class TransactionCleanupBoundary_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void FailedRelease_PreservesAnotherTransactionAndItsLock(bool failExplicit, bool writable)
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            database.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            var monitor = engine.GetMonitor();
            var locker = GetLocker(engine);
            // Query-only transactions do not occupy the explicit transaction slot.
            var query = monitor.GetTransaction(true, true, out _);
            var explicitTransaction = monitor.GetTransaction(true, false, out _);
            var failed = failExplicit ? explicitTransaction : query;
            var survivor = failExplicit ? query : explicitTransaction;
            var snapshot = failed.CreateSnapshot(writable ? LockMode.Write : LockMode.Read, "docs", false);
            InvalidateLease(snapshot);

            try
            {
                Action release = () => monitor.ReleaseTransaction(failed);
                release.Should().Throw<AggregateException>();
                failed.State.Should().Be(TransactionState.Disposed);
                monitor.Transactions.Should().ContainSingle().Which.Should().BeSameAs(survivor);
                monitor.GetThreadTransaction().Should().BeSameAs(survivor);
                monitor.GetTransaction(false, false, out _).Should().BeSameAs(failExplicit ? null : survivor);
                locker.IsInTransaction.Should().BeTrue("the other transaction still owns this thread's lock");
                locker.TransactionsCount.Should().Be(1);
                locker.TryEnterExclusive(out _).Should().BeFalse();
            }
            finally
            {
                monitor.ReleaseTransaction(survivor);
            }

            monitor.Transactions.Should().BeEmpty();
            monitor.GetThreadTransaction().Should().BeNull();
            locker.IsInTransaction.Should().BeFalse();
            locker.TransactionsCount.Should().Be(0);
            database.Checkpoint();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RepeatedCleanupFailures_DoNotExhaustRegistrationOrRetainFrames(bool writable)
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            var collection = database.GetCollection("docs");
            collection.Insert(new BsonDocument { ["_id"] = 1 });
            var monitor = engine.GetMonitor();
            var locker = GetLocker(engine);

            for (var i = 0; i < 200; i++)
            {
                var transaction = monitor.GetTransaction(true, i % 2 == 0, out _);
                var snapshot = transaction.CreateSnapshot(writable ? LockMode.Write : LockMode.Read, "docs", false);
                var cache = snapshot.CollectionPage.Buffer.Cache;
                InvalidateLease(snapshot);

                Action release = () => monitor.ReleaseTransaction(transaction);
                release.Should().Throw<AggregateException>();
                monitor.Transactions.Should().BeEmpty();
                monitor.GetThreadTransaction().Should().BeNull();
                locker.IsInTransaction.Should().BeFalse();
                cache.PinnedPages.Should().Be(0);
                cache.WritablePages.Should().Be(0);
                cache.LostFrames.Should().Be(0);
                Assert.NotNull(collection.FindById(1));
            }
            database.Checkpoint();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DisposeFalse_PreservesHealthyManagedLeasesAndRegistration(bool writable)
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            database.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetTransaction(true, false, out _);
            var snapshot = transaction.CreateSnapshot(writable ? LockMode.Write : LockMode.Read, "docs", false);
            var buffer = snapshot.CollectionPage.Buffer;
            var cache = buffer.Cache;
            var pins = cache.PinnedPages;
            var writes = cache.WritablePages;
            var generation = buffer.Generation;
            var dispose = typeof(TransactionService).GetMethod("Dispose", BindingFlags.NonPublic | BindingFlags.Instance);

            try
            {
                dispose.Invoke(transaction, new object[] { false });
                transaction.State.Should().Be(TransactionState.Active);
                monitor.Transactions.Should().ContainSingle().Which.Should().BeSameAs(transaction);
                snapshot.CollectionPage.Buffer.Should().BeSameAs(buffer);
                buffer.Generation.Should().Be(generation);
                cache.PinnedPages.Should().Be(pins);
                cache.WritablePages.Should().Be(writes);
            }
            finally
            {
                monitor.ReleaseTransaction(transaction);
            }
            cache.PinnedPages.Should().Be(0);
            cache.WritablePages.Should().Be(0);
        }

        [Fact]
        public void MonitorDispose_WithOneDamagedTransaction_CleansAllOtherTransactions()
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            database.GetCollection("broken").Insert(new BsonDocument { ["_id"] = 1 });
            database.GetCollection("healthy").Insert(new BsonDocument { ["_id"] = 2 });
            var monitor = engine.GetMonitor();
            var transactions = Enumerable.Range(0, 8).Select(index => monitor.GetTransaction(true, true, out _)).ToArray();
            var broken = transactions[0].CreateSnapshot(LockMode.Write, "broken", false);
            var cache = broken.CollectionPage.Buffer.Cache;
            foreach (var transaction in transactions.Skip(1))
            {
                transaction.CreateSnapshot(LockMode.Read, "healthy", false);
            }
            InvalidateLease(broken);

            Action close = monitor.Dispose;
            close.Should().Throw<AggregateException>();
            transactions.Should().OnlyContain(transaction => transaction.State == TransactionState.Disposed);
            monitor.Transactions.Should().BeEmpty();
            cache.PinnedPages.Should().Be(0);
            cache.WritablePages.Should().Be(0);
            cache.LostFrames.Should().Be(0);
            close.Should().NotThrow("subsequent disposal must be harmless");
            Action create = () => monitor.GetTransaction(true, false, out _);
            create.Should().Throw<ObjectDisposedException>();
        }

        [Fact]
        public async Task FailedSnapshotCleanup_ReleasesOtherSnapshotsAndTheirCollectionLocks()
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            foreach (var name in new[] { "broken", "writer", "reader" })
            {
                database.GetCollection(name).Insert(new BsonDocument { ["_id"] = 1 });
            }
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetTransaction(true, false, out _);
            var broken = transaction.CreateSnapshot(LockMode.Write, "broken", false);
            transaction.CreateSnapshot(LockMode.Write, "writer", false);
            transaction.CreateSnapshot(LockMode.Read, "reader", false);
            var cache = broken.CollectionPage.Buffer.Cache;
            InvalidateLease(broken);

            Action release = () => monitor.ReleaseTransaction(transaction);
            release.Should().Throw<AggregateException>();
            monitor.Transactions.Should().BeEmpty();
            cache.PinnedPages.Should().Be(0);
            cache.WritablePages.Should().Be(0);
            cache.LostFrames.Should().Be(0);

            var write = Task.Run(() =>
            {
                foreach (var name in new[] { "broken", "writer", "reader" })
                {
                    database.GetCollection(name).Insert(new BsonDocument { ["_id"] = 2 });
                }
            });
            (await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(write);
            await write;
        }

        private static void InvalidateLease(Snapshot snapshot)
        {
            if (snapshot.Mode == LockMode.Write) snapshot.CollectionPage.Buffer.Cache.DiscardPage(snapshot.CollectionPage.Buffer);
            else snapshot.CollectionPage.TakeBuffer().Release();
        }

        private static LockService GetLocker(LiteEngine engine) => (LockService)typeof(LiteEngine)
            .GetField("_locker", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);
    }
}
