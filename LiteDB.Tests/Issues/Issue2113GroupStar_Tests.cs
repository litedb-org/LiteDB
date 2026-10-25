using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2113GroupStar_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Group_star_preserves_members_and_query_reuse_with_having_and_paging(bool indexed)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["category"] = "a", ["payload"] = "one" },
                new BsonDocument { ["_id"] = 2, ["category"] = "a", ["payload"] = "two" },
                new BsonDocument { ["_id"] = 3, ["category"] = "b", ["payload"] = "three" }
            });
            if (indexed) rows.EnsureIndex("category");
            var selector = BsonExpression.Create("*");
            var query = rows.Query().GroupBy("category").Having(BsonExpression.Create("COUNT(*) >= @0", 1)).Select(selector);
            var first = query.ToArray();
            Assert.Equal(2, first.Length);
            Assert.Equal(new[] { 1, 2 }, first[0]["expr"].AsArray.Select(x => x["_id"].AsInt32));
            Assert.Equal("two", first[0]["expr"][1]["payload"].AsString);
            Assert.Equal(3, first[1]["expr"][0]["_id"].AsInt32);
            Assert.Equal("*", selector.Source);
            Assert.False(selector.IsScalar);
            Assert.Equal(2, query.ToArray().Length);
            Assert.Equal(3, query.Offset(1).Limit(1).ToArray().Single()["expr"][0]["_id"].AsInt32);
            Assert.Empty(rows.Query().Where("_id < 0").GroupBy("category").Select("*").ToArray());
        }
    }
}
