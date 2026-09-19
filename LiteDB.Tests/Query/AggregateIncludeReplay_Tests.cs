using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class AggregateIncludeReplay_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Ordinary_index_aggregates_replay_includes(bool filterIncluded, bool sorted)
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("owners").Insert(Enumerable.Range(1, 3).Select(id => new BsonDocument
            {
                ["_id"] = id, ["Name"] = "owner" + id, ["Weight"] = id * 10
            }));
            var docs = db.GetCollection("docs");
            docs.Insert(Enumerable.Range(1, 3).Select(id => new BsonDocument
            {
                ["_id"] = id, ["Category"] = id < 3 ? "keep" : "exclude",
                ["Owner"] = new BsonDocument { ["$id"] = id, ["$ref"] = "owners" }
            }));
            docs.EnsureIndex("category", "$.Category");
            var query = docs.Query().Include("$.Owner").Where("$.Category = 'keep'");
            if (filterIncluded) query.Where("$.Owner.Weight >= 20");
            if (sorted) query.OrderBy("$._id", Query.Descending);
            var aggregate = query.Select("{first: FIRST(*.Owner.Name), again: FIRST(*.Owner.Name), " +
                "total: SUM(*.Owner.Weight), repeatedTotal: SUM(*.Owner.Weight)}");

            aggregate.GetPlan()["index"]["name"].AsString.Should().Be("category");
            var result = aggregate.Single();
            result["first"].AsString.Should().BeOneOf("owner1", "owner2");
            if (filterIncluded || sorted) result["first"].AsString.Should().Be("owner2");
            result["again"].Should().Be(result["first"]);
            result["total"].AsInt32.Should().Be(filterIncluded ? 20 : 30);
            result["repeatedTotal"].AsInt32.Should().Be(filterIncluded ? 20 : 30);
        }
    }
}
