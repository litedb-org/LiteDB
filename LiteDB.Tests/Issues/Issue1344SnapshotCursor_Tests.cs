using System.Linq;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1344SnapshotCursor_Tests
    {
#if DEBUG || TESTING
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Read_cursor_keeps_its_view_across_write_upgrade_and_safepoint(bool commit)
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var db = new LiteDatabase(engine);
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, 64).Select(id => new BsonDocument
            {
                ["_id"] = id, ["value"] = "old", ["payload"] = new string('x', 3000)
            }));
            db.Checkpoint();
            Assert.True(db.BeginTrans());
            using (var cursor = rows.Find(Query.All()).GetEnumerator())
            {
                Assert.True(cursor.MoveNext());
                Assert.Equal("old", cursor.Current["value"].AsString);
                Assert.Equal(64, rows.UpdateMany("{ value: 'new' }", "_id > 0"));
                var transaction = engine.GetMonitor().GetThreadTransaction();
                transaction.MaxTransactionSize = 1;
                transaction.Safepoint();
                Assert.Equal(0, transaction.Pages.TransactionSize);
                var seen = 1;
                while (cursor.MoveNext())
                {
                    Assert.Equal(++seen, cursor.Current["_id"].AsInt32);
                    Assert.Equal("old", cursor.Current["value"].AsString);
                }
                Assert.Equal(64, seen);
            }
            if (commit) Assert.True(db.Commit());
            else Assert.True(db.Rollback());
            Assert.Equal(64, rows.Count());
            Assert.All(rows.FindAll(), row => Assert.Equal(commit ? "new" : "old", row["value"].AsString));
            db.Checkpoint();
        }
#endif
    }
}
