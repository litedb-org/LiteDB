using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class SetUnionPersistence_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Set_unions_keep_duplicate_keys_across_page_release_rollback_and_reopen(string password)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Password = password, CacheSize = 65536, TransactionPageLimit = 1 };
            const string predicate = "Score IN [10,10,15,70] OR Score BETWEEN 70 AND 80";
            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("rows");
                rows.InsertBulk(Enumerable.Range(1, 2000).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i % 100, ["Payload"] = new string('x', 800) }));
                rows.EnsureIndex("score", "Score");
                db.Checkpoint();
                db.BeginTrans();
                rows.DeleteMany(predicate).Should().Be(260);
                rows.Count(predicate).Should().Be(0);
                db.Rollback();
                rows.Count(predicate).Should().Be(260);
            }
            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("rows");
                var query = rows.Query().Where(predicate).OrderBy("Score", Query.Descending);
                query.GetPlan().ContainsKey("filters").Should().BeFalse();
                var expected = Enumerable.Range(1, 2000).Select(i => i % 100).Where(i => i == 10 || i == 15 || (i >= 70 && i <= 80))
                    .OrderByDescending(i => i).ToArray();
                query.ToArray().Select(x => x["Score"].AsInt32).Should().Equal(expected);
                query.Offset(210).Limit(40).ToArray().Select(x => x["Score"].AsInt32).Should().Equal(expected.Skip(210).Take(40));
            }
        }

        [Fact]
        public void Existing_bindings_use_rebuilt_collation_for_set_and_range_overlap()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(new ConnectionString { Filename = file.Filename, Collation = Collation.Binary }))
            {
                seed.GetCollection("rows").InsertBulk(new[] { "a", "A", "b", "B", "c" }.Select(value => new BsonDocument { ["Value"] = value }));
            }
            using var db = new LiteDatabase(file.Filename);
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("value", "Value");
            var template = BsonExpression.Create("Value IN @keys OR Value BETWEEN @low AND @high",
                new BsonDocument { ["keys"] = new BsonArray("a", "b", "b"), ["low"] = "b", ["high"] = "c" });
            rows.Find(template).Select(x => x["Value"].AsString).Should().BeEquivalentTo(new[] { "a", "b", "c" });
            db.Rebuild(new RebuildOptions { Collation = new Collation("en-US/IgnoreCase") });
            rows.Find(template).Select(x => x["Value"].AsString).Should().BeEquivalentTo(new[] { "a", "A", "b", "B", "c" });
        }
    }
}
