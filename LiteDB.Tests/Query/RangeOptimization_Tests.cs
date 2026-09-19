using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class RangeOptimization_Tests
    {
        [Theory]
        [InlineData("Score >= 3 AND Score < 7", 3, 4)]
        [InlineData("3 < Score AND 7 >= Score", 4, 4)]
        [InlineData("Score >= 1 AND Score > 3 AND Score <= 7 AND Score < 7", 4, 3)]
        [InlineData("Score >= 3 AND Score <= 3", 3, 1)]
        public void Scalar_ranges_use_both_bounds_and_remove_consumed_filters(string predicate, int start, int count)
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection<Row>("rows");
            var query = rows.Query().Where(predicate).OrderByDescending(x => x.Score);
            var plan = query.GetPlan();
            plan["index"]["mode"].AsString.Should().Contain("RANGE SCAN");
            plan.ContainsKey("filters").Should().BeFalse();
            query.ToArray().Select(x => x.Score).Should().Equal(Enumerable.Range(start, count).Reverse());
        }

        [Fact]
        public void Separate_linq_filters_keep_their_own_parameter_values()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection<Row>("rows");
            var low = 3;
            var high = 7;
            var query = rows.Query().Where(x => x.Score >= low).Where(x => x.Score < high).OrderBy(x => x.Score);
            query.GetPlan().ContainsKey("filters").Should().BeFalse();
            query.ToArray().Select(x => x.Score).Should().Equal(3, 4, 5, 6);
            low = 5;
            high = 9;
            rows.Query().Where(x => x.Score >= low && x.Score < high).ToArray().Select(x => x.Score)
                .Should().BeEquivalentTo(new[] { 5, 6, 7, 8 });
        }

        [Fact]
        public void Multikey_bounds_can_match_different_elements()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["Values"] = new BsonArray(1, 10) });
            rows.EnsureIndex("values", "Values[*]");
            var query = rows.Query().Where("Values[*] ANY > 8 AND Values[*] ANY < 3");
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            query.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(1);
        }

        [Fact]
        public void Case_insensitive_bounds_and_mixed_numeric_types_follow_bson_ordering()
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation("en-US/IgnoreCase") });
            var rows = db.GetCollection("rows");
            rows.InsertBulk(new[] { new BsonDocument { ["Name"] = "a", ["Score"] = 3 },
                new BsonDocument { ["Name"] = "A", ["Score"] = 3.0 }, new BsonDocument { ["Name"] = "b", ["Score"] = 4L } });
            rows.EnsureIndex("name", "Name");
            rows.EnsureIndex("score", "Score");
            rows.Query().Where("Name >= 'a' AND Name <= 'A'").Count().Should().Be(2);
            rows.Query().Where("Score >= 3.0 AND Score < 4").Count().Should().Be(2);
        }

        private static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new Row { Id = i, Score = i }));
            rows.EnsureIndex(x => x.Score);
            return db;
        }

        public class Row
        {
            public int Id { get; set; }
            public int Score { get; set; }
        }
    }
}
