using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Database close never waits for an executing handle call that is itself waiting for a lock
    /// of an idle handle the close has not rolled back yet; callers arriving during close see it.
    /// </summary>
    public class TransactionHandleCloseOrder_Tests
    {
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };

        private static CollectionLock RowsLock(LiteDatabase db)
        {
            var engine = (LiteEngine)typeof(LiteDatabase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(db);
            var locker = (LockService)typeof(LiteEngine).GetField("_locker", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);
            var collections = (ConcurrentDictionary<string, CollectionLock>)typeof(LockService)
                .GetField("_collections", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(locker);
            return collections["rows"];
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Close_rolls_back_idle_handles_before_waiting_for_executing_calls(bool busyBegunFirst)
        {
            using var file = new TempFile();
            var timeout = TimeSpan.FromSeconds(10);
            var db = new LiteDatabase(file) { Timeout = timeout };
            db.GetCollection("rows").Insert(Row(0));
            ILiteTransaction busy, idle;
            if (busyBegunFirst) { busy = db.BeginTransaction(); idle = db.BeginTransaction(); }
            else { idle = db.BeginTransaction(); busy = db.BeginTransaction(); }
            idle.GetCollection("rows").Insert(Row(1));
            using var waiting = new ManualResetEventSlim();
            RowsLock(db).BeforeWait = waiting.Set;
            Exception busyError = null;
            var call = new Thread(() =>
            {
                try { busy.GetCollection("rows").Insert(Row(2)); }
                catch (Exception error) { busyError = error; }
            });
            call.Start();
            Assert.True(waiting.Wait(TimeSpan.FromSeconds(10)));
            var elapsed = Stopwatch.StartNew();
            db.Dispose();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Close waited {elapsed.Elapsed} behind an idle handle's lock.");
            Assert.True(call.Join(TimeSpan.FromSeconds(10)));
            // The waiting call proceeds once the idle handle rolled back; close then rolls it back.
            Assert.Null(busyError);
            Assert.Equal(LiteTransactionState.RolledBack, idle.State);
            Assert.Equal(LiteTransactionState.RolledBack, busy.State);
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 0 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Handle_stays_disposable_after_an_interrupted_database_close(bool shared)
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file)) seed.GetCollection("rows").Insert(Row(0));
            var settings = new ConnectionString { Filename = file, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };
            var db = new LiteDatabase(settings);
            var tx = db.BeginTransaction();
            using var inside = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            System.Collections.Generic.IEnumerable<BsonDocument> Input()
            {
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(20));
                yield return Row(1);
            }
            Exception executingError = null;
            var executing = new Thread(() => executingError = Record.Exception(() => tx.GetCollection("rows").Insert(Input())));
            executing.Start();
            Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));
            Exception closeError = null;
            var closer = new Thread(() => closeError = Record.Exception(db.Dispose));
            closer.Start();
            var closing = typeof(LiteTransaction).GetField("_closing", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.True(SpinWait.SpinUntil(() => (bool)closing.GetValue(tx), TimeSpan.FromSeconds(10)));
            closer.Interrupt();
            Assert.True(closer.Join(TimeSpan.FromSeconds(10)));
            Assert.IsType<ThreadInterruptedException>(closeError);
            release.Set();
            Assert.True(executing.Join(TimeSpan.FromSeconds(10)));
            // The interrupted close went on to release the database's engine. A Direct handle
            // runs on that engine, so its call fails closed; a Shared handle has its own core.
            if (shared) Assert.True(executingError == null, executingError?.ToString());
            else Assert.Equal(LiteException.ENGINE_DISPOSED, Assert.IsType<LiteException>(executingError).ErrorCode);
            // The interrupted close never settled this handle: disposing it still rolls back
            // and releases its locks and, in Shared mode, the writer mutex.
            tx.Dispose();
            Assert.Equal(shared ? LiteTransactionState.RolledBack : LiteTransactionState.Failed, tx.State);
            using (var peer = new LiteDatabase(settings) { Timeout = TimeSpan.FromSeconds(5) })
            {
                var write = System.Threading.Tasks.Task.Run(() => peer.GetCollection("rows").Insert(Row(2)));
                Assert.True(write.Wait(TimeSpan.FromSeconds(20)), "The handle kept its ownership after Dispose.");
                write.GetAwaiter().GetResult();
            }
            try { db.Dispose(); } catch { }
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 0, 2 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
        }

        [Fact]
        public void Sequential_call_during_close_sees_the_close_not_an_overlap()
        {
            using var file = new TempFile();
            var db = new LiteDatabase(file);
            db.GetCollection("rows").Insert(Row(0));
            var tx = db.BeginTransaction();
            var rows = tx.GetCollection("rows");
            using var inside = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            System.Collections.Generic.IEnumerable<BsonDocument> Input()
            {
                yield return Row(1);
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(20));
            }
            // Close waits for this executing call; a later call from another thread lands during close.
            Exception executingError = null, closeError = null;
            var executing = new Thread(() => executingError = Record.Exception(() => rows.Insert(Input())));
            executing.Start();
            Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));
            var closer = new Thread(() => closeError = Record.Exception(db.Dispose));
            closer.Start();
            var closing = typeof(LiteTransaction).GetField("_closing", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.True(SpinWait.SpinUntil(() => (bool)closing.GetValue(tx), TimeSpan.FromSeconds(10)));
            Exception late = null;
            var sequential = new Thread(() => late = Record.Exception(() => rows.Count()));
            sequential.Start();
            Assert.True(sequential.Join(TimeSpan.FromSeconds(10)));
            release.Set();
            Assert.True(executing.Join(TimeSpan.FromSeconds(10)));
            Assert.True(closer.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(executingError);
            Assert.Null(closeError);
            Assert.IsType<ObjectDisposedException>(late);
            Assert.Equal(LiteTransactionState.RolledBack, tx.State);
        }
    }
}
