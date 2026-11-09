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
    public class TransactionHandleSharedWaitGuards_Tests
    {
        [OneFileTheory]
        [InlineData(false, false)] [InlineData(false, true)]
        [InlineData(true, false)] [InlineData(true, true)]
        public async Task Ordinary_and_legacy_waiters_timeout_without_borrowing_handle_then_progress(bool mapped, bool legacy)
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file)) seed.Timeout = TimeSpan.FromSeconds(1);
            using (var engine = new SharedEngine(new EngineSettings { Filename = file, SharedOneFile = mapped }))
            using (var peerEngine = new SharedEngine(new EngineSettings { Filename = file, SharedOneFile = mapped }))
            using (var db = new LiteDatabase(engine))
            using (var peer = new LiteDatabase(peerEngine))
            {
                _ = peer.Timeout;
                using var holder = db.BeginTransaction();
                holder.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                var error = await Task.Run(() => Assert.Throws<LiteException>(() =>
                {
                    if (legacy) peer.BeginTrans();
                    else peer.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                }));
                Assert.Equal(120, error.ErrorCode);
                Assert.Equal(LiteTransactionState.Active, holder.State);
                Assert.NotNull(holder.GetCollection("rows").FindById(1));
                holder.Commit();
                peer.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 });
            }
            using var cold = new LiteDatabase(file);
            Assert.NotNull(cold.GetCollection("rows").FindById(1));
            Assert.Null(cold.GetCollection("rows").FindById(2));
            Assert.NotNull(cold.GetCollection("rows").FindById(3));
        }

        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void Executing_callback_cannot_wait_for_its_own_handle_and_prior_writes_survive(bool mapped)
        {
            using var file = new TempFile();
            using (var engine = new SharedEngine(new EngineSettings { Filename = file, SharedOneFile = mapped }))
            using (var db = new LiteDatabase(engine))
            using (var tx = db.BeginTransaction())
            {
                var rows = tx.GetCollection<TransactionHandleBinding_Tests.CallbackEntity>("rows");
                rows.Insert(new TransactionHandleBinding_Tests.CallbackEntity { Id = 1, Reading = () =>
                {
                    Assert.Throws<InvalidOperationException>(() => db.BeginTransaction());
                    Assert.Throws<InvalidOperationException>(() => db.GetCollection("other").Insert(new BsonDocument { ["_id"] = 2 }));
                }});
                Assert.Equal(LiteTransactionState.Active, tx.State);
                tx.Commit();
            }
            using var cold = new LiteDatabase(file);
            Assert.NotNull(cold.GetCollection("rows").FindById(1));
            Assert.False(cold.CollectionExists("other"));
        }
    }
}
#endif
