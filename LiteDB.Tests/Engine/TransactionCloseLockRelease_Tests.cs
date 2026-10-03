using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Which collection locks a close or a foreign-thread cleanup releases: legacy transactions
    /// keep dev's owner-thread rule; a handle's locks are released, and woken waiters see the
    /// engine's published error.
    /// </summary>
    public class TransactionCloseLockRelease_Tests
    {
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };

        private static CollectionLock RowsLock(LiteEngine engine)
        {
            var locker = (LockService)typeof(LiteEngine).GetField("_locker", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);
            var collections = (ConcurrentDictionary<string, CollectionLock>)typeof(LockService)
                .GetField("_collections", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(locker);
            return collections["rows"];
        }

        private static LiteEngine EngineOf(LiteDatabase db)
        {
            object engine = typeof(LiteDatabase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(db);
            return engine as LiteEngine ?? (LiteEngine)engine.GetType()
                .GetField("_inner", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);
        }

#pragma warning disable CS0618
        [Fact]
        public void Legacy_close_keeps_a_foreign_threads_collection_lock_as_before()
        {
            // dev behavior: closing on another thread does not release an idle legacy
            // transaction's collection lock under a writer waiting for it; the writer times out.
            using var file = new TempFile();
            var db = new LiteDatabase(file) { Timeout = TimeSpan.FromSeconds(1) };
            db.GetCollection("rows").Insert(Row(0));
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var waiting = new ManualResetEventSlim();
            Exception ownerError = null;
            var owner = new Thread(() =>
            {
                try
                {
                    db.BeginTrans();
                    db.GetCollection("rows").Insert(Row(1));
                }
                catch (Exception error) { ownerError = error; }
                finally { held.Set(); }
                release.Wait(TimeSpan.FromSeconds(20));
                try { db.Commit(); } catch { }
            });
            owner.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            Assert.Null(ownerError);
            RowsLock(EngineOf(db)).BeforeWait = waiting.Set;
            Exception waiterError = null;
            var waiter = new Thread(() =>
            {
                try { db.GetCollection("rows").Insert(Row(2)); }
                catch (Exception error) { waiterError = error; }
            });
            waiter.Start();
            Assert.True(waiting.Wait(TimeSpan.FromSeconds(10)));
            db.Dispose();
            Assert.True(waiter.Join(TimeSpan.FromSeconds(10)));
            release.Set();
            Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(LiteException.LOCK_TIMEOUT, Assert.IsType<LiteException>(waiterError).ErrorCode);
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 0 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
        }

        [Fact]
        public void Foreign_thread_cursor_dispose_keeps_the_legacy_lock_as_on_dev()
        {
            // dev behavior (a known leak, tracked separately): a ForUpdate cursor disposed on a
            // thread other than its owner leaves the collection lock held; writers time out.
            using var file = new TempFile();
            using var db = new LiteDatabase(file) { Timeout = TimeSpan.FromSeconds(1) };
            db.GetCollection("rows").Insert(Row(1));
            IBsonDataReader reader = null;
            var opener = new Thread(() => { reader = db.GetCollection("rows").Query().ForUpdate().ExecuteReader(); reader.Read(); });
            opener.Start();
            Assert.True(opener.Join(TimeSpan.FromSeconds(10)));
            var disposer = new Thread(() => reader.Dispose());
            disposer.Start();
            Assert.True(disposer.Join(TimeSpan.FromSeconds(10)));
            Exception writerError = null;
            var writer = new Thread(() =>
            {
                try { db.GetCollection("rows").Insert(Row(2)); }
                catch (Exception error) { writerError = error; }
            });
            writer.Start();
            Assert.True(writer.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(LiteException.LOCK_TIMEOUT, Assert.IsType<LiteException>(writerError).ErrorCode);
        }
#pragma warning restore CS0618

        [Fact]
        public void Legacy_owner_finishing_after_close_sees_the_disposal_not_corruption()
        {
            // An ordinary or legacy commit racing close releases its transaction after the
            // monitor closed: dev reported ObjectDisposedException, never INVALID_DATAFILE_STATE.
            using var file = new TempFile();
            var engine = new LiteEngine(new EngineSettings { Filename = file });
            engine.Insert("rows", new[] { Row(0) }, BsonAutoId.Int32);
            var monitor = engine.GetMonitor();
            Exception release = null;
            using var begun = new ManualResetEventSlim();
            using var closed = new ManualResetEventSlim();
            var owner = new Thread(() =>
            {
                engine.BeginTrans();
                engine.Insert("rows", new[] { Row(1) }, BsonAutoId.Int32);
                var transaction = monitor.GetTransactionsSnapshot().Single();
                begun.Set();
                closed.Wait(TimeSpan.FromSeconds(20));
                release = Record.Exception(() => monitor.ReleaseTransaction(transaction));
            });
            owner.Start();
            Assert.True(begun.Wait(TimeSpan.FromSeconds(10)));
            engine.Dispose();
            closed.Set();
            Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
            Assert.IsType<ObjectDisposedException>(release);
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 0 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Handle_locks_released_by_close_wake_waiters_with_the_published_error(string password)
        {
            using var file = new TempFile();
            var engine = new LiteEngine(new EngineSettings { Filename = file, Password = password });
            var db = new LiteDatabase(engine, disposeOnClose: false) { Timeout = TimeSpan.FromSeconds(30) };
            db.GetCollection("rows").Insert(Row(0));
            var tx = db.BeginTransaction();
            tx.GetCollection("rows").Insert(Row(1));
            using var waiting = new ManualResetEventSlim();
            RowsLock(engine).BeforeWait = waiting.Set;
            Exception waiterError = null;
            var waiter = new Thread(() =>
            {
                try { db.GetCollection("rows").Insert(Row(2)); }
                catch (Exception error) { waiterError = error; }
            });
            waiter.Start();
            Assert.True(waiting.Wait(TimeSpan.FromSeconds(10)));
            var closer = new Thread(() => engine.Dispose());
            closer.Start();
            Assert.True(closer.Join(TimeSpan.FromSeconds(10)));
            Assert.True(waiter.Join(TimeSpan.FromSeconds(2)), "A waiter behind a closed handle's lock was not woken.");
            var published = Assert.IsType<LiteException>(waiterError);
            Assert.Equal(LiteException.ENGINE_DISPOSED, published.ErrorCode);
            try { tx.Dispose(); } catch { }
            try { db.Dispose(); } catch { }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
                Assert.Equal(new[] { 0 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
            }
        }
    }
}
