using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleCallbackLock_Tests
    {
        public sealed class CallbackRow
        {
            [BsonIgnore] public Action Callback;
            public int Id { get; set; }
            public int Value { get { Callback?.Invoke(); return Id * 10; } set { } }
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void Same_collection_callback_refuses_self_wait_without_enlisting_independent_work(string password, bool handoff)
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file, Password = password };
            using (var db = new LiteDatabase(settings))
            {
                Seed(db);
                db.Timeout = TimeSpan.FromSeconds(1);
                using var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                var collectionLock = RowsLock(db);
                var waited = false;
                collectionLock.BeforeWait = () => waited = true;
                var callbackRan = false;
                Action write = () => tx.GetCollection<CallbackRow>("rows").Insert(new CallbackRow { Id = 3, Callback = () =>
                {
                    callbackRan = true;
                    Assert.Equal(LiteException.LOCK_TIMEOUT,
                        Assert.Throws<LiteException>(() => db.GetCollection("rows").Insert(Row(4))).ErrorCode);
                    Assert.NotNull(db.GetCollection("rows").FindById(1));
                    Assert.Null(db.GetCollection("rows").FindById(2));
                    // A different collection is legitimate independent callback work.
                    db.GetCollection("ordinary").Insert(Row(5));
                }});
                if (handoff)
                {
                    Exception failure = null;
                    var worker = new Thread(() => { try { write(); } catch (Exception error) { failure = error; } });
                    worker.Start();
                    Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
                    Assert.Null(failure);
                }
                else write();
                Assert.True(callbackRan);
                Assert.False(waited);
                Assert.Equal(LiteTransactionState.Active, tx.State);
                tx.Rollback();
            }
            Verify(settings, new[] { 1 }, true);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Original_thread_may_wait_for_idle_handle_completed_by_another_thread(string password)
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file, Password = password };
            using (var db = new LiteDatabase(settings))
            {
                Seed(db);
                using var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                using var waiting = new ManualResetEventSlim();
                RowsLock(db).BeforeWait = waiting.Set;
                Exception failure = null;
                var completing = new Thread(() =>
                {
                    try
                    {
                        Assert.True(waiting.Wait(TimeSpan.FromSeconds(5)));
                        tx.Commit();
                    }
                    catch (Exception error) { failure = error; }
                }) { IsBackground = true };
                completing.Start();
                db.GetCollection("rows").Insert(Row(3));
                Assert.True(completing.Join(TimeSpan.FromSeconds(5)));
                Assert.Null(failure);
                Assert.True(waiting.IsSet);
                Assert.Equal(LiteTransactionState.Committed, tx.State);
            }
            Verify(settings, new[] { 1, 2, 3 }, false);
        }

        [Fact]
        public void Repeated_public_handoff_restores_binding_before_admitting_next_callback()
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file };
            using (var db = new LiteDatabase(settings))
            {
                Seed(db);
                using var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                var rows = tx.GetCollection<CallbackRow>("rows");
                RowsLock(db).BeforeWait = () => throw new InvalidOperationException("Callback attempted self-wait");
                using var ready = new AutoResetEvent(false);
                using var completed = new AutoResetEvent(false);
                Exception failure = null;
                var worker = new Thread(() =>
                {
                    try
                    {
                        for (var i = 0; i < 200; i++)
                        {
                            Assert.True(ready.WaitOne(TimeSpan.FromSeconds(5)));
                            var callbackRan = false;
                            while (!callbackRan)
                            {
                                try
                                {
                                    rows.Insert(new CallbackRow { Id = 10 + i, Callback = () =>
                                    {
                                        callbackRan = true;
                                        Assert.Equal(LiteException.LOCK_TIMEOUT,
                                            Assert.Throws<LiteException>(() => db.GetCollection("rows").Insert(Row(999))).ErrorCode);
                                    }});
                                }
                                catch (InvalidOperationException) when (!callbackRan && tx.State == LiteTransactionState.Active)
                                { Thread.Yield(); } // The preceding operation has not released admission yet.
                            }
                            completed.Set();
                        }
                    }
                    catch (Exception error) { failure = error; completed.Set(); }
                }) { IsBackground = true };
                worker.Start();
                for (var i = 0; i < 200; i++)
                {
                    ((LiteTransaction)tx).Run(() => { ready.Set(); return true; });
                    Assert.True(completed.WaitOne(TimeSpan.FromSeconds(5)));
                    Assert.Null(failure);
                }
                Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
                Assert.Equal(LiteTransactionState.Active, tx.State);
                tx.Rollback();
            }
            Verify(settings, new[] { 1 }, false);
        }

        private static CollectionLock RowsLock(LiteDatabase db)
        {
            var engine = (LiteEngine)typeof(LiteDatabase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(db);
            var locker = (LockService)typeof(LiteEngine).GetField("_locker", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);
            var collections = (ConcurrentDictionary<string, CollectionLock>)typeof(LockService)
                .GetField("_collections", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(locker);
            return collections["rows"];
        }
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["Value"] = id * 10 };
        private static void Seed(LiteDatabase db)
        {
            db.GetCollection("rows").Insert(Row(1));
            db.GetCollection("rows").EnsureIndex("Value");
            db.GetCollection("sentinel").Insert(Row(9));
            db.Checkpoint();
        }
        private static void Verify(ConnectionString settings, int[] ids, bool ordinary)
        {
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var db = new LiteDatabase(settings);
                var query = db.GetCollection("rows").Query().Where(Query.GTE("Value", 10));
                Assert.Equal("Value", query.GetPlan()["index"]["name"].AsString);
                Assert.Equal(ids, query.ToArray().Select(row => row["_id"].AsInt32).OrderBy(id => id));
                Assert.Equal(ids.Length, db.GetCollection("rows").Count());
                Assert.Equal(ordinary ? 1 : 0, db.GetCollection("ordinary").Count());
                if (ordinary) Assert.NotNull(db.GetCollection("ordinary").FindById(5));
                Assert.NotNull(db.GetCollection("sentinel").FindById(9));
            }
        }
    }
}
