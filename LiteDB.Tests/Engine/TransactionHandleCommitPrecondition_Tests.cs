using System;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleCommitPrecondition_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Open_cursor_commit_refusal_keeps_handle_active_and_completion_releases_locks(bool shared, bool rollback)
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file,
                Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };
            using (var db = new LiteDatabase(settings))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 10 });
                rows.EnsureIndex("value");
                db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
                using var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = 20 });
                using (var reader = tx.GetCollection("rows").Query().ExecuteReader())
                {
                    Assert.True(reader.Read());
                    Assert.Throws<InvalidOperationException>(tx.Commit);
                    Assert.Equal(LiteTransactionState.Active, tx.State);
                    Assert.True(reader.Read());
                }
                if (rollback) tx.Rollback(); else tx.Commit();
                Assert.Equal(rollback ? LiteTransactionState.RolledBack : LiteTransactionState.Committed, tx.State);
                // Reuse the same collection while the originating facade is still alive:
                // retained refcounts cannot hide an abandoned writer transaction here.
                using var next = db.BeginTransaction();
                next.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3, ["value"] = 30 });
                next.Commit();
                Assert.Equal(rollback ? 2 : 3, rows.Count());
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var db = new LiteDatabase(settings);
                var query = db.GetCollection("rows").Query().Where(Query.GTE("value", 10));
                Assert.Equal("value", query.GetPlan()["index"]["name"].AsString);
                var ids = rollback ? new[] { 1, 3 } : new[] { 1, 2, 3 };
                Assert.Equal(ids, query.ToArray().Select(row => row["_id"].AsInt32).OrderBy(id => id));
                Assert.Equal(ids.Length, db.GetCollection("rows").Count());
                Assert.NotNull(db.GetCollection("sentinel").FindById(9));
            }
        }
    }
}
