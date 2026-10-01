using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
#pragma warning disable CS0618
    public class TransactionHandleDatabaseClose_Tests
    {
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };

        private static ConnectionString Settings(TempFile file, bool shared) => new ConnectionString
        { Filename = file, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };

        [Fact]
        public void Close_over_caller_owned_engine_rolls_back_handles_and_keeps_the_engine_usable()
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(file.Filename))
            {
                var db = new LiteDatabase(engine, disposeOnClose: false);
                db.GetCollection("rows").Insert(Row(1));
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                var reader = tx.GetCollection("rows").Query().ExecuteReader();
                Assert.True(reader.Read());
                db.Dispose();
                Assert.Equal(LiteTransactionState.RolledBack, tx.State);
                Assert.Equal(0, db.TransactionHandles.ActiveCount);
                Assert.Throws<ObjectDisposedException>(() => reader.Read());
                Assert.Throws<ObjectDisposedException>(() => db.BeginTransaction());
                tx.Dispose();

                // The handle's locks and admission lease are gone: ordinary writes proceed.
                using var next = new LiteDatabase(engine, disposeOnClose: false);
                next.GetCollection("rows").Insert(Row(3));
                Assert.Equal(new[] { 1, 3 }, next.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
            }
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 1, 3 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
        }

        [Fact]
        public async Task Shared_begin_waiting_behind_a_handle_is_refused_after_close_settles_the_owner()
        {
            using var file = new TempFile();
            var settings = Settings(file, shared: true);
            using (var seed = new LiteDatabase(settings)) seed.GetCollection("rows").Insert(Row(1));
            var db = new LiteDatabase(settings);
            var owner = db.BeginTransaction();
            owner.GetCollection("rows").Insert(Row(2));
            var waiting = Task.Run(() => db.BeginTransaction());
            await Task.Delay(200);
            Assert.False(waiting.IsCompleted);

            db.Dispose();
            Assert.Equal(LiteTransactionState.RolledBack, owner.State);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal(0, db.TransactionHandles.ActiveCount);

            // Neither handle retains native writer ownership.
            using (var peer = new LiteDatabase(settings))
            {
                using var tx = peer.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(3));
                tx.Commit();
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var cold = new LiteDatabase(new ConnectionString { Filename = file });
                Assert.Equal(new[] { 1, 3 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
            }
        }

        [Fact]
        public void Rebuild_inside_handle_callback_fails_fast_instead_of_waiting_for_the_handle_lease()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file))
            {
                db.GetCollection("rows").Insert(Row(1));
                db.Timeout = TimeSpan.FromSeconds(30);
                using var tx = db.BeginTransaction();
                LiteException refusal = null;
                var elapsed = Stopwatch.StartNew();
                System.Collections.Generic.IEnumerable<BsonDocument> Input()
                {
                    refusal = Assert.Throws<LiteException>(() => db.Rebuild());
                    elapsed.Stop();
                    yield return Row(2);
                }
                tx.GetCollection("rows").Insert(Input());
                Assert.Equal(LiteException.LOCK_TIMEOUT, refusal.ErrorCode);
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"Rebuild waited {elapsed.Elapsed}");
                Assert.Equal(LiteTransactionState.Active, tx.State);
                tx.Commit();
                // Outside the callback exclusive maintenance proceeds.
                db.Rebuild();
            }
            using var cold = new LiteDatabase(file);
            Assert.Equal(2, cold.GetCollection("rows").Count());
        }

        [Fact]
        public async Task Callback_read_is_not_queued_behind_rebuild_waiting_for_the_handle()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file))
            {
                db.GetCollection("rows").Insert(Row(1));
                db.Timeout = TimeSpan.FromSeconds(30);
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                var rebuild = Task.Run(() => db.Rebuild());
                var gate = Field(Field(Field(db, "_engine"), "_locker"), "_transaction");
                var waited = Stopwatch.StartNew();
                while ((int)Field(gate, "_waitingWriters") == 0)
                {
                    Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "Rebuild did not queue behind the handle");
                    Thread.Sleep(5);
                }
                var elapsed = Stopwatch.StartNew();
                System.Collections.Generic.IEnumerable<BsonDocument> Input()
                {
                    // The queued writer waits for this handle; this read must not wait for the writer.
                    Assert.NotNull(db.GetCollection("rows").FindById(1));
                    elapsed.Stop();
                    yield return Row(3);
                }
                tx.GetCollection("rows").Insert(Input());
                Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"Callback read waited {elapsed.Elapsed}");
                Assert.False(rebuild.IsCompleted);
                tx.Commit();
                await rebuild.WaitAsync(TimeSpan.FromSeconds(30));
                tx.Dispose();
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var cold = new LiteDatabase(file);
                Assert.Equal(new[] { 1, 2, 3 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
            }
        }

        [Fact]
        public void Owned_engine_is_disposed_even_when_settling_a_handle_fails()
        {
            using var file = new TempFile();
            var engine = new TrackingEngine(new EngineSettings { Filename = file.Filename });
            var db = new LiteDatabase(engine);
            db.GetCollection("rows").Insert(Row(1));
            var tx = db.BeginTransaction();
            tx.GetCollection("rows").Insert(Row(2));
            var cleanup = new System.IO.IOException("rollback cleanup failed");
            var monitor = engine.GetMonitor();
            monitor.AfterTransactionExit = () => { monitor.AfterTransactionExit = null; throw cleanup; };
            Assert.Same(cleanup, Assert.Throws<System.IO.IOException>(db.Dispose));
            Assert.True(engine.Disposed);
            Assert.Equal(LiteTransactionState.Failed, tx.State);
            tx.Dispose();
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 1 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
        }

        private sealed class TrackingEngine : LiteEngine
        {
            internal bool Disposed;
            internal TrackingEngine(EngineSettings settings) : base(settings) { }
            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }

        private static object Field(object owner, string name) => owner.GetType()
            .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(owner);

        [Fact]
        public void Legacy_completion_diagnostics_ignore_handles_and_handles_ignore_other_threads_legacy_state()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file))
            {
                ILiteTransaction tx = null;
                TransactionHandle_Tests.OnThread(() =>
                {
                    tx = db.BeginTransaction();
                    tx.GetCollection("handle").Insert(Row(1));
                });
                // Only an explicit legacy transaction of another thread makes legacy Commit foreign.
                Assert.False(db.Commit());
                Assert.False(db.Rollback());

                Assert.True(db.BeginTrans());
                db.GetCollection("legacy").Insert(Row(2));
                Assert.Throws<InvalidOperationException>(() => db.BeginTransaction());
                // A legacy transaction on another thread does not block a new handle here.
                TransactionHandle_Tests.OnThread(() =>
                {
                    using var other = db.BeginTransaction();
                    other.GetCollection("other").Insert(Row(3));
                    other.Commit();
                });
                Assert.True(db.Commit());

                Assert.Equal(LiteTransactionState.Active, tx.State);
                TransactionHandle_Tests.OnThread(tx.Commit);
            }
            using var cold = new LiteDatabase(file);
            Assert.Equal(1, cold.GetCollection("handle").Count());
            Assert.Equal(1, cold.GetCollection("legacy").Count());
            Assert.Equal(1, cold.GetCollection("other").Count());
        }
    }
#pragma warning restore CS0618
}
