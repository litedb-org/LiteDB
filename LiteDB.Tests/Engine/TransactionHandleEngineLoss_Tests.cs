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
                else
                {
                    Assert.NotEqual(LiteTransactionState.Active, tx.State);
                    // The engine's published failure, never the monitor's raw disposal.
                    Assert.False(error is ObjectDisposedException, error.ToString());
                }
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
    }
}
