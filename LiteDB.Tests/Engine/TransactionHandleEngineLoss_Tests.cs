using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// A handle whose engine transaction is lost (a peer stopped or closed the engine) must never
    /// report success or fall back to an automatic transaction; begin reports the engine's failure.
    /// </summary>
    public class TransactionHandleEngineLoss_Tests
    {
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };

        private static TransactionService HandleTransaction(LiteEngine engine) =>
            engine.GetMonitor().GetTransactionsSnapshot().Single(t => t.Owner.Explicit != null);

        [Fact]
        public void Commit_after_the_engine_lost_the_transaction_fails_instead_of_returning()
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("rows").Insert(Row(1));
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                // The state a peer close leaves between the engine's lookup and its state check.
                HandleTransaction(engine).Dispose();
                Assert.IsType<InvalidOperationException>(Record.Exception(tx.Commit));
                Assert.Equal(LiteTransactionState.Failed, tx.State);
                Assert.Equal(0, db.TransactionHandles.ActiveCount);
                tx.Dispose();
            }
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 1 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
        }

        [Fact]
        public void Bound_write_after_the_engine_lost_the_transaction_never_runs_automatically()
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("rows").Insert(Row(1));
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(2));
                // As engine close does: dispose the transaction and clear the handle's slot.
                var lost = HandleTransaction(engine);
                lost.Dispose();
                lost.Owner.Slot.Transaction = null;
                Assert.ThrowsAny<Exception>(() => tx.GetCollection("rows").Insert(Row(3)));
                Assert.Equal(LiteTransactionState.Failed, tx.State);
                Assert.Throws<InvalidOperationException>(() => tx.GetCollection("rows").Insert(Row(4)));
                tx.Dispose();
                Assert.Equal(new[] { 1 }, db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
            }
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 1 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
        }

        [Fact]
        public void Commit_racing_a_raw_engine_close_never_returns_while_active()
        {
            var outcomes = new int[Enum.GetValues(typeof(LiteTransactionState)).Length];
            for (var i = 0; i < 400; i++)
            {
                var engine = new LiteEngine(new EngineSettings { DataStream = new MemoryStream() });
                var db = new LiteDatabase(engine, disposeOnClose: false);
                var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(i));
                using var go = new ManualResetEventSlim();
                var spin = i % 200;
                var closer = new Thread(() =>
                {
                    go.Wait();
                    for (var k = 0; k < spin; k++) Thread.SpinWait(1);
                    try { engine.Dispose(); } catch { }
                });
                closer.Start();
                go.Set();
                var error = Record.Exception(tx.Commit);
                Assert.True(closer.Join(TimeSpan.FromSeconds(10)));
                // Returning normally means committed; otherwise the outcome is never Active.
                if (error == null) Assert.Equal(LiteTransactionState.Committed, tx.State);
                else Assert.NotEqual(LiteTransactionState.Active, tx.State);
                outcomes[(int)tx.State]++;
                try { tx.Dispose(); } catch { }
                try { db.Dispose(); } catch { }
            }
            Assert.Equal(0, outcomes[(int)LiteTransactionState.Active]);
        }

        [Fact]
        public void Begin_after_engine_dispose_reports_the_engine_disposal()
        {
            var engine = new LiteEngine(new EngineSettings { DataStream = new MemoryStream() });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            engine.Dispose();
            var ordinary = Record.Exception(() => db.GetCollection("rows").Count());
            var begin = Record.Exception(() => db.BeginTransaction());
            Assert.IsType<LiteException>(ordinary);
            Assert.IsType<LiteException>(begin);
            Assert.Equal(((LiteException)ordinary).ErrorCode, ((LiteException)begin).ErrorCode);
        }

        [Fact]
        public void Begin_after_a_fatal_stop_reports_the_published_failure()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings { Filename = file });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("rows").Insert(Row(1));
            engine.SimulateDiskWriteFail = _ => throw new IOException("injected write failure");
            Assert.ThrowsAny<Exception>(() => db.GetCollection("rows").Insert(Row(2)));
            engine.SimulateDiskWriteFail = null;
            var ordinary = Record.Exception(() => db.GetCollection("rows").Count());
            var begin = Record.Exception(() => db.BeginTransaction());
            Assert.NotNull(ordinary);
            Assert.Equal(ordinary.GetType(), begin?.GetType());
            Assert.Equal(ordinary.Message, begin.Message);
        }

#pragma warning disable CS0618
        [Fact]
        public void Legacy_close_keeps_a_foreign_threads_collection_lock_as_before()
        {
            // Restored dev behavior: closing on another thread does not release an idle legacy
            // transaction's collection lock under a writer waiting for it; the writer times out.
            using var file = new TempFile();
            var db = new LiteDatabase(file) { Timeout = TimeSpan.FromSeconds(1) };
            db.GetCollection("rows").Insert(Row(0));
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var owner = new Thread(() =>
            {
                db.BeginTrans();
                db.GetCollection("rows").Insert(Row(1));
                held.Set();
                release.Wait(TimeSpan.FromSeconds(20));
                try { db.Commit(); } catch { }
            });
            owner.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            Exception waiterError = null;
            var waiter = new Thread(() =>
            {
                try { db.GetCollection("rows").Insert(Row(2)); }
                catch (Exception error) { waiterError = error; }
            });
            waiter.Start();
            Thread.Sleep(300);
            db.Dispose();
            Assert.True(waiter.Join(TimeSpan.FromSeconds(10)));
            release.Set();
            Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
            Assert.Equal(LiteException.LOCK_TIMEOUT, Assert.IsType<LiteException>(waiterError).ErrorCode);
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 0 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
        }

        [Fact]
        public void Interrupted_shared_begin_releases_the_writer_ownership_its_holder_acquires()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file)) seed.GetCollection("rows").Insert(Row(1));
            var shared = new ConnectionString { Filename = file, Connection = ConnectionType.Shared };
            using var owner = new LiteDatabase(shared);
            using var db = new LiteDatabase(shared);
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var legacy = new Thread(() =>
            {
                owner.BeginTrans();
                owner.GetCollection("rows").Insert(Row(2));
                held.Set();
                release.Wait(TimeSpan.FromSeconds(20));
                owner.Commit();
            });
            legacy.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            Exception beginError = null;
            var begin = new Thread(() =>
            {
                try { db.BeginTransaction().Dispose(); }
                catch (Exception error) { beginError = error; }
            });
            begin.Start();
            // The begin waits for its holder's native admission behind the legacy owner.
            Thread.Sleep(500);
            begin.Interrupt();
            Assert.True(begin.Join(TimeSpan.FromSeconds(10)));
            Assert.IsType<ThreadInterruptedException>(beginError);
            release.Set();
            Assert.True(legacy.Join(TimeSpan.FromSeconds(10)));
            // Once its holder acquires and releases, other connections write and begin again.
            using var probe = new LiteDatabase(shared);
            var write = Task.Run(() => probe.GetCollection("rows").Insert(Row(3)));
            Assert.True(write.Wait(TimeSpan.FromSeconds(20)), "The interrupted begin's holder kept writer ownership.");
            var next = Task.Run(() => { using var tx = probe.BeginTransaction(); tx.GetCollection("rows").Insert(Row(4)); tx.Commit(); });
            Assert.True(next.Wait(TimeSpan.FromSeconds(20)), "The interrupted begin kept the local handle queue.");
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 1, 2, 3, 4 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
        }
#pragma warning restore CS0618
    }
}
