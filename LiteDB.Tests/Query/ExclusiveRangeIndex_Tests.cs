using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class ExclusiveRangeIndex_Tests
    {
        [Theory]
        [InlineData("Score > 0", Query.Ascending)]
        [InlineData("Score < 0", Query.Descending)]
        [InlineData("Score > 0 AND Score < 2", Query.Ascending)]
        [InlineData("Score > -2 AND Score < 0", Query.Descending)]
        [InlineData("Score >= 0", Query.Ascending)]
        [InlineData("Score <= 0", Query.Descending)]
        [InlineData("Score > 1", Query.Ascending)]
        [InlineData("Score < -1", Query.Descending)]
        public void Duplicate_boundary_seeks_keep_inclusivity_and_direction(string predicate, int order)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 2000).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Score"] = i % 100 == 0 ? i % 3 - 1 : 0
            }));
            var expected = rows.Query().Where(predicate).OrderBy("Score", order).ToArray().Select(x => x["Score"]).ToArray();
            rows.EnsureIndex("score", "Score");
            rows.Query().Where(predicate).OrderBy("Score", order).ToArray().Select(x => x["Score"]).Should().Equal(expected);
            rows.Query().Where(predicate).OrderBy("Score", order).Offset(2).Limit(4).ToArray()
                .Select(x => x["Score"]).Should().Equal(expected.Skip(2).Take(4));
            rows.Count(predicate).Should().Be(expected.Length);
        }

        [Theory]
        [InlineData("Name > 'a'", Query.Ascending)]
        [InlineData("Name < 'B'", Query.Descending)]
        public void Exclusive_seeks_skip_all_collation_equivalent_keys(string predicate, int order)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation("en-US/IgnoreCase") });
            var rows = db.GetCollection("rows");
            rows.InsertBulk(new[] { "a", "A", "b", "B", "c" }.Select(s => new BsonDocument { ["Name"] = s }));
            var expected = order == Query.Ascending ? new[] { "b", "B", "c" } : new[] { "a", "A" };
            rows.EnsureIndex("name", "Name");
            rows.Query().Where(predicate).OrderBy("Name", order).ToArray().Select(x => x["Name"].AsString).Should().BeEquivalentTo(expected);
        }

        [Theory]
        [InlineData("Value > @0", Query.Ascending)]
        [InlineData("Value < @0", Query.Descending)]
        public void Exclusive_seeks_preserve_bson_type_order(string text, int order)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            var values = new BsonValue[] { BsonValue.Null, -1, 0, 0L, 0.0, 0m, 1, "x", new BsonArray(1, 2) };
            rows.InsertBulk(values.Select((v, i) => new BsonDocument { ["_id"] = i + 1, ["Value"] = v }));
            var bounds = values.Concat(new[] { BsonValue.MinValue, BsonValue.MaxValue }).ToArray();
            var expected = bounds.Select(v => rows.Find(BsonExpression.Create(text, v)).Select(x => x["_id"]).ToArray()).ToArray();
            rows.EnsureIndex("value", "Value");
            for (var i = 0; i < bounds.Length; i++)
            {
                rows.Query().Where(BsonExpression.Create(text, bounds[i])).OrderBy("Value", order).ToArray()
                    .Select(x => x["_id"]).Should().BeEquivalentTo(expected[i]);
            }
        }
    }
}
