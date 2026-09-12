using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class TransactionCleanup_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FailedRelease_RemovesRegistrationAndLocks_AndAllowsFurtherQueries(bool queryOnly)
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            var collection = database.GetCollection("docs");
            collection.Insert(new BsonDocument { ["_id"] = 1 });
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetTransaction(true, queryOnly, out _);
            var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
            var cache = snapshot.CollectionPage.Buffer.Cache;
            cache.DiscardPage(snapshot.CollectionPage.Buffer);

            Action release = () => monitor.ReleaseTransaction(transaction);
            release.Should().Throw<AggregateException>();

            monitor.Transactions.Should().BeEmpty();
            monitor.GetThreadTransaction().Should().BeNull();
            cache.WritablePages.Should().Be(0);
            for (var i = 0; i < 200; i++)
            {
                Assert.NotNull(collection.FindById(1));
            }
            // The transaction read lock must also be gone, so a checkpoint can
            // acquire the exclusive lock on this thread.
            database.Checkpoint();
            // A different thread must be able to acquire the collection lock.
            var write = Task.Run(() => collection.Insert(new BsonDocument { ["_id"] = 2 }));
            (await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(write);
            await write;
        }

        [Fact]
        public void DisposeFalse_DoesNotTouchManagedState_EvenWithAStaleLease()
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            database.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetTransaction(true, false, out _);
            var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
            var cache = snapshot.CollectionPage.Buffer.Cache;
            cache.DiscardPage(snapshot.CollectionPage.Buffer);
            var dispose = typeof(TransactionService).GetMethod("Dispose", BindingFlags.NonPublic | BindingFlags.Instance);

            Action finalize = () => dispose.Invoke(transaction, new object[] { false });
            finalize.Should().NotThrow();
            transaction.State.Should().Be(TransactionState.Active);
            monitor.Transactions.Should().ContainSingle().Which.Should().BeSameAs(transaction);

            Action release = () => monitor.ReleaseTransaction(transaction);
            release.Should().Throw<AggregateException>();
            monitor.Transactions.Should().BeEmpty();
        }

        [Fact]
        public void AbandonedTransaction_OnExitedThread_IsCleanedByExplicitEngineClose()
        {
            using var data = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { DataStream = data });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            database.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            MemoryCache cache = null;
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    database.BeginTrans();
                    var transaction = engine.GetMonitor().GetThreadTransaction();
                    cache = transaction.CreateSnapshot(LockMode.Read, "docs", false).CollectionPage.Buffer.Cache;
                }
                catch (Exception ex) { error = ex; }
            });
            thread.Start();
            thread.Join();
            error.Should().BeNull();

            Collect();
            engine.GetMonitor().Transactions.Should().ContainSingle("the monitor owns cleanup, not the finalizer thread");
            engine.Close().Should().BeEmpty();
            engine.GetMonitor().Transactions.Should().BeEmpty();
            cache.TotalPages.Should().Be(0);
        }

        [Fact]
        public void AbandonedEngine_WithDamagedTransaction_IsCollectibleWithoutFinalizerCleanup()
        {
            WeakReference reference = null;
            var thread = new Thread(() => reference = AbandonEngine());
            thread.Start();
            thread.Join();
            Collect();
            reference.IsAlive.Should().BeFalse();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference AbandonEngine()
        {
            var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            var database = new LiteDatabase(engine, disposeOnClose: false);
            database.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            database.BeginTrans();
            var transaction = engine.GetMonitor().GetThreadTransaction();
            var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
            snapshot.CollectionPage.Buffer.Cache.DiscardPage(snapshot.CollectionPage.Buffer);
            return new WeakReference(transaction);
        }

        private static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
