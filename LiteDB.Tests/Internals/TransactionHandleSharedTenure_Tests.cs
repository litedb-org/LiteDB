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
