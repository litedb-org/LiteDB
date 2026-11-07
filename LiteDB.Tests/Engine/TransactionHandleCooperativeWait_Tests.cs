using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleCooperativeWait_Tests
    {
        internal static LockService Locker(LiteEngine engine) => (LockService)typeof(LiteEngine)
            .GetField("_locker", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);
        internal static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id };

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Idle_handle_waits_even_on_creator_thread_and_foreign_completion_wakes_it(bool peer, bool ordinary)
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var other = peer ? new LiteDatabase(file.Filename) : null;
            var target = other ?? db;
            db.GetCollection("rows").Insert(Row(0));
            using var holder = db.BeginTransaction();
            holder.GetCollection("rows").Insert(Row(1));
            var collection = Collection(engine, "rows");
            using var waiting = new ManualResetEventSlim();
            collection.BeforeWait = waiting.Set;
            var release = Task.Run(() =>
            {
                Assert.True(waiting.Wait(TimeSpan.FromSeconds(5)));
                holder.Commit();
            });
            try
            {
                if (ordinary) target.GetCollection("rows").Insert(Row(2));
                else
                {
                    using var next = target.BeginTransaction();
                    next.GetCollection("rows").Insert(Row(2));
                    next.Commit();
                }
            }
            finally { Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
            Assert.Equal(new[] { 0, 1, 2 }, target.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Idle_handle_on_same_creator_thread_times_out_with_120_and_preserves_holder(bool peer)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file);
            using var other = peer ? new LiteDatabase(file) : null;
            db.Timeout = TimeSpan.FromSeconds(1);
            using var holder = db.BeginTransaction();
            holder.GetCollection("rows").Insert(Row(1));
            using var next = (other ?? db).BeginTransaction();
            Assert.Equal(120, Assert.Throws<LiteException>(() => next.GetCollection("rows").Insert(Row(2))).ErrorCode);
            Assert.Equal(LiteTransactionState.Failed, next.State);
            Assert.Equal(LiteTransactionState.Active, holder.State);
            holder.Commit();
            Assert.Equal(1, db.GetCollection("rows").Count());
        }

        internal static CollectionLock Collection(LiteEngine engine, string name)
        {
            var locks = (System.Collections.Concurrent.ConcurrentDictionary<string, CollectionLock>)typeof(LockService)
                .GetField("_collections", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(Locker(engine));
            return locks[name];
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Callback_conflicting_independent_work_refuses_without_aborting_holder(bool ordinary)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file);
            using var holder = db.BeginTransaction();
            using var next = db.BeginTransaction();
            holder.GetCollection("rows").Insert(Row(1));
            var rows = holder.GetCollection<TransactionHandleBinding_Tests.CallbackEntity>("rows");
            rows.Insert(new TransactionHandleBinding_Tests.CallbackEntity { Id = 2, Reading = () =>
            {
                var error = Record.Exception(() =>
                {
                    if (ordinary) db.GetCollection("rows").Insert(Row(3));
                    else next.GetCollection("rows").Insert(Row(3));
                });
                Assert.IsAssignableFrom<InvalidOperationException>(error);
                Assert.Contains("callback", error.Message);
                Assert.Equal(LiteTransactionState.Active, next.State);
                Assert.Equal(LiteTransactionState.Active, holder.State);
            }});
            next.Rollback();
            holder.Commit();
            Assert.Equal(2, db.GetCollection("rows").Count());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Legacy_foreign_thread_holder_waits_then_releases_to_handle(bool peer)
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var other = peer ? new LiteDatabase(file.Filename) : null;
            db.GetCollection("rows").Insert(Row(0));
            using var held = new ManualResetEventSlim();
            using var waiting = new ManualResetEventSlim();
            Collection(engine, "rows").BeforeWait = waiting.Set;
            var owner = Task.Run(() =>
            {
                db.BeginTrans();
                db.GetCollection("rows").Insert(Row(1));
                held.Set();
                Assert.True(waiting.Wait(TimeSpan.FromSeconds(5)));
                db.Commit();
            });
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
            using var next = (other ?? db).BeginTransaction();
            next.GetCollection("rows").Insert(Row(2));
            next.Commit();
            Assert.True(owner.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(3, db.GetCollection("rows").Count());
        }
    }
}
