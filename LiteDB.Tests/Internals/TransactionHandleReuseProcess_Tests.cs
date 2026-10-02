#if !NETFRAMEWORK
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Internals
{
    /// <summary>Reused Shared holder threads and wrappers (#3083) against writers of another process.</summary>
    [Collection(NativeFileSyncCollection.Name)]
    public class TransactionHandleReuseProcess_Tests
    {
        private const int Rows = 64;

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Another_process_writes_within_its_bound_and_reused_handles_never_read_stale_state(string password)
        {
            using var file = new TempFile();
            // The process handshakes can take longer than the production idle expiry.
            using (var pool = new TransactionHolderScheduler_Tests.PoolScope())
            using (var shared = new SharedEngine(new EngineSettings { Filename = file, Password = password }))
            using (var db = new LiteDatabase(shared, disposeOnClose: false))
            {
                LiteEngine previousCore;
                using (var seed = db.BeginTransaction())
                {
                    previousCore = ReuseAccess.Core(seed);
                    var rows = seed.GetCollection("rows");
                    rows.Insert(Enumerable.Range(0, Rows).Select(id =>
                        new BsonDocument { ["_id"] = id, ["value"] = 0, ["payload"] = new string('x', 3000) }));
                    rows.EnsureIndex("idx0", "$.value");
                    seed.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9, ["value"] = "untouched" });
                    seed.Commit();
                }
                var child = shared.CachedTransactionChild;
                Assert.NotNull(child);
                TransactionHolderScheduler_Tests.Idle(child.HolderThread);
                var holder = child.HolderThread;
                for (var round = 1; round <= 3; round++)
                {
                    // The peer's Shared connection waits at most 1 s for writer ownership. It commits,
                    // replaces the index and checkpoints, so the data file itself changed meanwhile.
                    await MvccProcess.Run("handle-reuse-peer", file, password, round.ToString());
                    using (var tx = db.BeginTransaction())
                    {
                        Assert.Null(shared.CachedTransactionChild);
                        Assert.Same(holder, child.HolderThread);
                        Assert.NotSame(previousCore, ReuseAccess.Core(tx));
                        previousCore = ReuseAccess.Core(tx);
                        VerifyRound(tx.GetCollection("rows"), tx.GetCollection("sentinel"), round);
                        tx.GetCollection("handles").Insert(new BsonDocument { ["_id"] = round });
                        tx.Commit();
                    }
                    Assert.Same(child, shared.CachedTransactionChild);
                    TransactionHolderScheduler_Tests.Idle(holder);
                }
                Assert.Equal(1, shared.TransactionChildrenCreated);
                Assert.Equal(3, shared.TransactionChildrenReused);
                Assert.Equal(4, child.EngineOpens);
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
                VerifyRound(cold.GetCollection("rows"), cold.GetCollection("sentinel"), 3);
                Assert.Equal(new[] { 1, 2, 3 }, cold.GetCollection("handles").FindAll().Select(row => row["_id"].AsInt32).OrderBy(id => id));
            }
        }

        private static void VerifyRound(ILiteCollection<BsonDocument> rows, ILiteCollection<BsonDocument> sentinel, int round)
        {
            var documents = rows.FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
            Assert.Equal(Enumerable.Range(0, Rows), documents.Select(row => row["_id"].AsInt32));
            Assert.All(documents, row =>
            {
                Assert.Equal(round, row["value"].AsInt32);
                Assert.Equal(new string('x', 3000), row["payload"].AsString);
            });
            // The peer's index replacement is visible: the query seeks the new index.
            var query = rows.Query().Where(Query.EQ("value", round));
            var index = query.GetPlan()["index"];
            Assert.Equal("idx" + round, index["name"].AsString);
            Assert.StartsWith("INDEX SEEK", index["mode"].AsString);
            Assert.Equal(Enumerable.Range(0, Rows), query.ToArray().Select(row => row["_id"].AsInt32).OrderBy(id => id));
            Assert.Empty(rows.Find(Query.EQ("value", round - 1)));
            Assert.Equal("untouched", sentinel.FindById(9)["value"].AsString);
        }
    }
}
#endif
