using System;
using System.Collections.Generic;
using System.Threading;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Tests.Engine.TransactionHandleSharedCallback_Tests;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleSharedCallbackControl_Tests
    {
#if NET8_0_OR_GREATER
        [MappedTheory]
        [InlineData(null)] [InlineData("secret")]
        public void Coordinated_same_database_read_stays_independent_inside_shared_callback(string password)
        {
            using var file = new MappedTestFile();
            Seed(file, password);
            using (var receiver = new SharedEngine(Settings(file, password))
                { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
            using (var ordinary = new LiteDatabase(receiver))
            using (var owner = new LiteDatabase(new SharedEngine(Settings(file, password))))
            {
                for (var i = 0; i < 4; i++) Assert.NotNull(ordinary.GetCollection("rows").FindById(1));
                Assert.True(receiver.CoordinatedReadHits > 0);
                Assert.True(receiver.HasCachedSnapshot);
                Turnstile(receiver).BeforeMainWait = () => throw new OperationCanceledException("Unexpected native admission");
                using (var tx = owner.BeginTransaction())
                {
                    tx.GetCollection("rows").Insert(Row(3));
                    var hits = receiver.CoordinatedReadHits;
                    IEnumerable<BsonDocument> Input()
                    {
                        Assert.Equal(10, ordinary.GetCollection("rows").FindById(1)["value"].AsInt32);
                        Assert.Null(ordinary.GetCollection("rows").FindById(3));
                        yield return Row(4);
                    }
                    tx.GetCollection("rows").Insert(Input());
                    Assert.Equal(hits + 2, receiver.CoordinatedReadHits);
                    Assert.Equal(LiteTransactionState.Active, tx.State);
                    tx.Rollback();
                }
                // The tripwire covers the callback only; later waits are legitimate.
                Turnstile(receiver).BeforeMainWait = null;
                PeerWrite(owner);
            }
            Verify(file, password, new[] { 1, 2, 5 });
        }
#endif

        [Theory]
        [InlineData(null)] [InlineData("secret")]
        public void Uncaught_callback_refusal_rolls_back_and_releases_execution_dependency(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using (var shared = new SharedEngine(Settings(file, password)))
            using (var db = new LiteDatabase(shared))
            using (var tx = db.BeginTransaction())
            {
                Turnstile(shared).BeforeMainWait = () => throw new OperationCanceledException("Unexpected native admission");
                tx.GetCollection("rows").Insert(Row(3));
                IEnumerable<BsonDocument> Input()
                {
                    var version = db.UserVersion;
                    yield return Row(version + 4);
                }
                Assert.Throws<InvalidOperationException>(() => tx.GetCollection("rows").Insert(Input()));
                Assert.Equal(LiteTransactionState.Failed, tx.State);
                Turnstile(shared).BeforeMainWait = null;
                Assert.Equal(0, db.UserVersion);
                PeerWrite(db);
            }
            Verify(file, password, new[] { 1, 2, 5 });
        }

        [Theory]
        [InlineData(null, false)] [InlineData("secret", false)]
        [InlineData(null, true)] [InlineData("secret", true)]
        public void Nested_other_database_handle_keeps_outer_dependency_and_restores_it(string password, bool otherShared)
        {
            using var first = new TempFile();
            using var second = new TempFile();
            Seed(first, password);
            Seed(second, password);
            using (var aEngine = new SharedEngine(Settings(first, password)))
            using (var a = new LiteDatabase(aEngine))
            using (var b = new LiteDatabase(new ConnectionString { Filename = second, Password = password,
                Connection = otherShared ? ConnectionType.Shared : ConnectionType.Direct }))
            using (var outer = a.BeginTransaction())
            {
                // Ordinary work on the other database legitimately acquires its own mutex.
                Turnstile(aEngine).BeforeMainWait = () => throw new OperationCanceledException("outer native self-wait");
                IEnumerable<BsonDocument> Nested()
                {
                    Assert.Throws<InvalidOperationException>(() => a.UserVersion);
                    if (otherShared) Assert.Throws<InvalidOperationException>(() => b.UserVersion);
                    else b.GetCollection("independent").Insert(Row(7));
                    yield return Row(4);
                }
                IEnumerable<BsonDocument> Input()
                {
                    // Ordinary work in another Shared namespace remains independent.
                    Assert.Equal(0, b.UserVersion);
                    using (var inner = b.BeginTransaction())
                    {
                        inner.GetCollection("rows").Insert(Row(3));
                        inner.GetCollection("rows").Insert(Nested());
                        Assert.Equal(LiteTransactionState.Active, inner.State);
                        inner.Rollback();
                    }
                    // The inner scope is gone, while the enclosing Shared handle remains bound.
                    Assert.Equal(0, b.UserVersion);
                    Assert.Throws<InvalidOperationException>(() => a.UserVersion);
                    yield return Row(4);
                }
                outer.GetCollection("rows").Insert(Row(3));
                outer.GetCollection("rows").Insert(Input());
                Assert.Equal(LiteTransactionState.Active, outer.State);
                outer.Rollback();
                Turnstile(aEngine).BeforeMainWait = null;
                PeerWrite(a);
                PeerWrite(b);
            }
            Verify(first, password, new[] { 1, 2, 5 });
            Verify(second, password, new[] { 1, 2, 5 });
            using var cold = new LiteDatabase(new ConnectionString { Filename = second, Password = password });
            Assert.Equal(otherShared ? 0 : 1, cold.GetCollection("independent").Count());
            if (!otherShared) Assert.Equal(70, cold.GetCollection("independent").FindById(7)["value"].AsInt32);
        }

        [Theory]
        [InlineData(null)] [InlineData("secret")]
        public void Ordinary_other_database_callback_cannot_erase_enclosing_shared_dependency(string password)
        {
            using var first = new TempFile();
            using var second = new TempFile();
            Seed(first, password);
            Seed(second, password);
            using (var engine = new SharedEngine(Settings(first, password)))
            using (var a = new LiteDatabase(engine))
            using (var b = new LiteDatabase(new ConnectionString { Filename = second, Password = password }))
            using (var tx = a.BeginTransaction())
            {
                Turnstile(engine).BeforeMainWait = () => throw new OperationCanceledException("suppressed native self-wait");
                IEnumerable<BsonDocument> Inner()
                {
                    Assert.Throws<InvalidOperationException>(() => a.UserVersion);
                    yield return Row(3);
                }
                IEnumerable<BsonDocument> Input()
                {
                    b.GetCollection("rows").Insert(Inner());
                    yield return Row(3);
                }
                tx.GetCollection("rows").Insert(Input());
                tx.Rollback();
                Turnstile(engine).BeforeMainWait = null;
                PeerWrite(a);
                PeerWrite(b);
            }
            Verify(first, password, new[] { 1, 2, 5 });
            Verify(second, password, new[] { 1, 2, 3, 5 });
        }

        [Theory]
        [InlineData(null)] [InlineData("secret")]
        public void Ordinary_wait_for_idle_handle_can_finish_after_cross_thread_completion(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using (var engine = new SharedEngine(Settings(file, password)))
            using (var db = new LiteDatabase(engine))
            using (var tx = db.BeginTransaction())
            using (var waiting = new ManualResetEventSlim())
            {
                tx.GetCollection("rows").Insert(Row(3));
                Turnstile(engine).BeforeMainWait = () => waiting.Set();
                Exception failure = null;
                var completing = new Thread(() =>
                {
                    try { Assert.True(waiting.Wait(TimeSpan.FromSeconds(5))); tx.Commit(); }
                    catch (Exception error) { failure = error; }
                }) { IsBackground = true };
                completing.Start();
                // Enter/Exit from earlier handle operations must leave no ambient dependency.
                Assert.Equal(0, db.UserVersion);
                Assert.True(completing.Join(TimeSpan.FromSeconds(10)));
                Assert.True(waiting.IsSet);
                Assert.Null(failure);
                Assert.Equal(LiteTransactionState.Committed, tx.State);
                PeerWrite(db);
            }
            Verify(file, password, new[] { 1, 2, 3, 5 });
        }
    }
}
