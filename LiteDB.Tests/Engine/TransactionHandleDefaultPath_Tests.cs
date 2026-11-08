using System;
using System.IO;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleDefaultPath_Tests
    {
        [Theory]
        [InlineData(false, false)] [InlineData(false, true)]
        [InlineData(true, false)] [InlineData(true, true)]
        public void Default_and_opt_out_handoff_rollback_and_close_preserve_indexed_rows(bool optOut, bool encrypted)
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file, Connection = ConnectionType.Shared,
                Password = encrypted ? "secret" : null, TransactionPageLimit = 1 };
            if (optOut) settings.SharedOneFile = false;
            using (var db = new LiteDatabase(settings))
            {
                db.GetCollection("rows").EnsureIndex("value", true);
                db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
                ILiteTransaction tx = null;
                TransactionHandle_Tests.OnThread(() => tx = db.BeginTransaction());
                using (tx)
                {
                    TransactionHandle_Tests.OnThread(() => tx.GetCollection("rows").Insert(Row(1)));
                    TransactionHandle_Tests.OnThread(tx.Commit);
                    Assert.Equal(LiteTransactionState.Committed, tx.State);
                }
                using (var aborted = db.BeginTransaction())
                {
                    TransactionHandle_Tests.OnThread(() => aborted.GetCollection("rows").Insert(Row(2)));
                    TransactionHandle_Tests.OnThread(aborted.Rollback);
                }
                var abandoned = db.BeginTransaction();
                TransactionHandle_Tests.OnThread(() => abandoned.GetCollection("rows").Insert(Row(3)));
                db.Dispose();
                Assert.Equal(LiteTransactionState.RolledBack, abandoned.State);
                abandoned.Dispose();
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var db = new LiteDatabase(settings);
                Assert.Equal(new[] { 1 }, db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32));
                Assert.Equal(1, db.GetCollection("rows").Count(Query.EQ("value", 10)));
                Assert.NotNull(db.GetCollection("sentinel").FindById(9));
            }
            if (optOut) Assert.True(Directory.Exists(file + "-readers"));
        }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };
    }
}
