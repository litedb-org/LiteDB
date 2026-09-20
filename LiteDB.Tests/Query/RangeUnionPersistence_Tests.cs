using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class RangeUnionPersistence_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Ordered_unions_survive_page_release_rollback_and_reopen(string password)
        {
            using var file = new TempFile();
            var connection = new ConnectionString
            {
                Filename = file.Filename, Password = password, CacheSize = 65536, TransactionPageLimit = 1
            };
            const string predicate = "(Score >= 10 AND Score < 20) OR (Score > 70 AND Score <= 80) OR Score = 15";
            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("rows");
                rows.InsertBulk(Enumerable.Range(1, 2000).Select(i => new BsonDocument
                {
                    ["_id"] = i, ["Score"] = i % 100, ["Payload"] = new string('x', 800)
                }));
                rows.EnsureIndex("score", "Score");
                db.Checkpoint();
                rows.Query().Where(predicate).GetPlan()["index"]["mode"].AsString.Should().Contain("RANGE UNION");
                db.BeginTrans();
                rows.DeleteMany(predicate).Should().Be(400);
                rows.Count(predicate).Should().Be(0);
                db.Rollback();
                rows.Count(predicate).Should().Be(400);
            }
            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("rows");
                foreach (var order in new[] { Query.Ascending, Query.Descending })
                {
                    var query = rows.Query().Where(predicate).OrderBy("Score", order);
                    var expected = Enumerable.Range(1, 2000).Select(i => i % 100)
                        .Where(i => (i >= 10 && i < 20) || (i > 70 && i <= 80)).OrderBy(i => i).ToArray();
                    if (order == Query.Descending) expected = expected.Reverse().ToArray();
                    var results = query.ToArray();
                    results.Select(x => x["Score"].AsInt32).Should().Equal(expected);
                    results.Select(x => x["_id"].AsInt32).Distinct().Count().Should().Be(400);
                    query.Offset(190).Limit(30).ToArray().Select(x => x["Score"].AsInt32).Should().Equal(expected.Skip(190).Take(30));
                }
            }
        }

        [Fact]
        public void Rebuilt_collation_changes_range_merging_for_an_existing_template()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(new ConnectionString { Filename = file.Filename, Collation = Collation.Binary }))
            {
                seed.GetCollection("rows").InsertBulk(new[] { "a", "A", "b", "B", "c" }.Select(value => new BsonDocument { ["Value"] = value }));
            }
            using var db = new LiteDatabase(file.Filename);
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("value", "Value");
            var template = BsonExpression.Create("(Value >= @low AND Value < @high) OR (Value > @other AND Value <= @end)",
                new BsonDocument { ["low"] = "a", ["high"] = "b", ["other"] = "B", ["end"] = "c" });
            rows.Find(template).Select(x => x["Value"].AsString).Should().BeEquivalentTo(new[] { "a", "b", "c" });
            db.Rebuild(new RebuildOptions { Collation = new Collation("en-US/IgnoreCase") });
            rows.Find(template).Select(x => x["Value"].AsString).Should().BeEquivalentTo(new[] { "a", "A", "c" });
        }
    }
}
