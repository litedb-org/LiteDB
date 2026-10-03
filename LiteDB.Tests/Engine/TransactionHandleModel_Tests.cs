using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>The expected committed state is a dictionary, independent of engine snapshots.</summary>
    public class TransactionHandleModel_Tests
    {
        [Theory]
        [InlineData(false, 1323064)]
        [InlineData(false, 30673041)]
        [InlineData(true, 1323064)]
        [InlineData(true, 30673041)]
        public void Seeded_handoffs_match_committed_model_after_each_cold_reopen(bool shared, int seed)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file, TransactionPageLimit = 1,
                Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };
            var committed = new Dictionary<int, int>();
            var random = new Random(seed);
            using (var db = new LiteDatabase(connection))
            {
                db.GetCollection("rows").EnsureIndex("value");
                db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 42 });
            }
            for (var round = 0; round < 12; round++)
            {
                using (var db = new LiteDatabase(connection))
                using (var tx = db.BeginTransaction())
                {
                    var expected = new Dictionary<int, int>(committed);
                    var rows = tx.GetCollection("rows");
                    for (var step = 0; step < 12; step++)
                    {
                        var id = random.Next(1, 20);
                        var value = random.Next(0, 8);
                        var remove = random.Next(3) == 0;
                        TransactionHandle_Tests.OnThread(() =>
                        {
                            if (remove) { rows.Delete(id); expected.Remove(id); }
                            else { rows.Upsert(Row(id, value)); expected[id] = value; }
                            AssertRows(expected, rows);
                        });
                    }
                    if (round % 3 == 0)
                    {
                        TransactionHandle_Tests.OnThread(tx.Commit);
                        committed = expected;
                    }
                    else if (round % 3 == 1) TransactionHandle_Tests.OnThread(tx.Rollback);
                    // Third arm intentionally exercises rollback by disposal.
                }
                using var cold = new LiteDatabase(connection);
                AssertRows(committed, cold.GetCollection("rows"));
                Assert.NotNull(cold.GetCollection("sentinel").FindById(42));
            }
        }

        private static BsonDocument Row(int id, int value) => new BsonDocument
        { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 10000) };
        private static void AssertRows(Dictionary<int, int> expected, ILiteCollection<BsonDocument> rows)
        {
            Assert.Equal(expected.OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value),
                rows.FindAll().OrderBy(x => x["_id"].AsInt32).Select(x => x["_id"].AsInt32 + ":" + x["value"].AsInt32));
            for (var value = 0; value < 8; value++)
                Assert.Equal(expected.Where(x => x.Value == value).Select(x => x.Key).OrderBy(x => x),
                    rows.Find(Query.EQ("value", value)).Select(x => x["_id"].AsInt32).OrderBy(x => x));
        }

        [Fact]
        public void Same_thread_independent_handles_cannot_recurse_into_the_same_collection_write_lock()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file);
            db.Timeout = TimeSpan.FromSeconds(1);
            using var first = db.BeginTransaction();
            using var second = db.BeginTransaction();
            first.GetCollection("rows").Insert(Row(1, 10));
            Assert.Throws<LiteException>(() => second.GetCollection("rows").Insert(Row(2, 20)));
            Assert.Equal(LiteTransactionState.Failed, second.State);
            Assert.Equal(LiteTransactionState.Active, first.State);
            first.Commit();
            Assert.Equal(new[] { 1 }, db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
        }
    }
}
