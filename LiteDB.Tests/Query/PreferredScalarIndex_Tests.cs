using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class PreferredScalarIndex_Tests
    {
        [Theory]
        [InlineData("Score")]
        [InlineData("Score.Value")]
        [InlineData("Tags[*]")]
        public void Canonical_preferred_fields_keep_documents_with_repeated_scalar_keys(string field)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(0, 30).Select(i => new BsonDocument { [field] = i % 3 }));
            rows.EnsureIndex("literal", x => x[field]);
            var query = rows.Query().Select(x => new { Value = x[field] });
            query.GetPlan()["index"]["name"].AsString.Should().Be("literal");
            query.ToArray().Select(x => x.Value.AsInt32).Should().Equal(
                Enumerable.Range(0, 3).SelectMany(i => Enumerable.Repeat(i, 10)));
            var path = "@." + BsonExpressionFormatter.PathField(field);
            rows.Query().Select("{ n: COUNT(*." + path + ") }").ToArray().Single()["n"].AsInt32.Should().Be(30);
        }

        [Fact]
        public void Preferred_scalar_array_keys_remain_one_result_per_document()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(1, 2) });
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(1, 2) });
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(2, 3) });
            rows.EnsureIndex("values", "Values");
            var query = rows.Query().Select(x => new { Value = x["Values"] });
            query.GetPlan()["index"]["name"].AsString.Should().Be("values");
            query.ToArray().Select(x => x.Value.AsArray.ToString()).Should().Equal("[1,2]", "[1,2]", "[2,3]");
        }
    }
}
