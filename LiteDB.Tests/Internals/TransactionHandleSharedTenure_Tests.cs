#if NET8_0_OR_GREATER
using System;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Tests.Engine;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class TransactionHandleSharedTenure_Tests
    {
        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void External_engine_close_fences_late_completion_and_detaches_the_failed_handle(bool mapped)
        {
            using var file = new TempFile();
            using (var engine = new SharedEngine(new EngineSettings { Filename = file, SharedOneFile = mapped }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                using var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                engine.Dispose();
                Assert.ThrowsAny<Exception>(tx.Commit);
                Assert.Equal(LiteTransactionState.Failed, tx.State);
                Assert.Equal(0, db.TransactionHandles.ActiveCount);
            }
            using var cold = new LiteDatabase(file);
            Assert.Null(cold.GetCollection("rows").FindById(1));
        }

        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void Failed_spill_discards_the_core_releases_the_handle_and_next_generation_recovers(bool mapped)
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file))
            {
                seed.GetCollection("rows").EnsureIndex("value");
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 10 });
                seed.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
            }
            using (var engine = new SharedEngine(new EngineSettings { Filename = file, SharedOneFile = mapped, TransactionPageLimit = 1 }))
            using (var db = new LiteDatabase(engine))
            {
                using var failed = db.BeginTransaction();
                var core = ((LiteTransaction)failed).Storage;
                var injected = new System.IO.IOException("Shared handle spill failure");
                var hits = 0;
                core.SimulateDiskWriteFail = _ => { hits++; throw injected; };
                Exception error;
                try
                {
                    error = Record.Exception(() =>
                    {
                        var rows = failed.GetCollection("rows");
                        rows.Insert(new BsonDocument { ["_id"] = 2, ["value"] = 20, ["payload"] = new string('x', 50000) });
                        // Safepoint precedes the next document; the first document
                        // leaves the actual dirty pages for this injected write.
                        rows.Insert(new BsonDocument { ["_id"] = 4 });
                    });
                }
                finally { core.SimulateDiskWriteFail = null; }
                Assert.NotNull(error);
                Assert.True(hits > 0);
                Assert.Equal(LiteTransactionState.Failed, failed.State);
                Assert.Equal(0, db.TransactionHandles.ActiveCount);
                using var next = db.BeginTransaction();
                Assert.NotSame(core, ((LiteTransaction)next).Storage);
                Assert.Null(next.GetCollection("rows").FindById(2));
                next.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3, ["value"] = 30 });
                next.Commit();
            }
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(file);
                Assert.NotNull(cold.GetCollection("rows").FindById(1));
                Assert.Null(cold.GetCollection("rows").FindById(2));
                Assert.Equal(1, cold.GetCollection("rows").Count(Query.EQ("value", 30)));
                Assert.NotNull(cold.GetCollection("sentinel").FindById(9));
            }
        }

        [OneFileTheory]
        [InlineData(false, false)] [InlineData(true, false)]
        [InlineData(false, true)] [InlineData(true, true)]
        public void Retained_pin_survives_creator_exit_and_releases_once_after_foreign_completion(bool mapped, bool readOnly)
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file)) seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            using (var engine = new SharedEngine(new EngineSettings { Filename = file, SharedOneFile = mapped, ReadOnly = readOnly }))
            using (var db = new LiteDatabase(engine))
            {
                ILiteTransaction tx = null;
                TransactionHandle_Tests.OnThread(() => tx = db.BeginTransaction());
                var pin = engine.Pin;
                Assert.NotNull(pin);
                Thread.Sleep(150); // Beyond legacy idle expiry: the logical hold must survive.
                TransactionHandle_Tests.OnThread(() => Assert.NotNull(tx.GetCollection("rows").FindById(1)));
                Assert.Same(pin, engine.Pin);
                if (!readOnly) TransactionHandle_Tests.OnThread(() => tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 }));
                TransactionHandle_Tests.OnThread(tx.Commit);
                Assert.Equal(LiteTransactionState.Committed, tx.State);
                tx.Dispose();
                Assert.Equal(0, db.TransactionHandles.ActiveCount);
            }
            using var cold = new LiteDatabase(file);
            Assert.Equal(readOnly ? 1 : 2, cold.GetCollection("rows").Count());
        }

        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public async Task Waiting_handle_times_out_without_stealing_live_generation_and_then_reuses(bool mapped)
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file)) seed.Timeout = TimeSpan.FromSeconds(1);
            using (var engine = new SharedEngine(new EngineSettings { Filename = file, SharedOneFile = mapped }))
            using (var db = new LiteDatabase(engine))
            using (var peerEngine = new SharedEngine(new EngineSettings { Filename = file, SharedOneFile = mapped }))
            using (var peer = new LiteDatabase(peerEngine))
            {
                _ = peer.Timeout;
                using var holder = db.BeginTransaction();
                var pin = engine.Pin;
                var failed = await Task.Run(() => Assert.Throws<LiteException>(() => peer.BeginTransaction()));
                Assert.Equal(120, failed.ErrorCode);
                Assert.Same(pin, engine.Pin);
                holder.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                holder.Commit();
                using var next = peer.BeginTransaction();
                Assert.NotNull(next.GetCollection("rows").FindById(1));
                next.Rollback();
            }
            using var cold = new LiteDatabase(file);
            Assert.NotNull(cold.GetCollection("rows").FindById(1));
        }
    }
}
#endif
