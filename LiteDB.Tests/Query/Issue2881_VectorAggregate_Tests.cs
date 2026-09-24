using System.Linq;
using FluentAssertions;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class Issue2881_VectorAggregate_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Repeated_aggregates_preserve_included_documents(bool topK, bool filterIncluded)
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("owners").Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["Name"] = "first", ["Weight"] = 10 },
                new BsonDocument { ["_id"] = 2, ["Name"] = "second", ["Weight"] = 20 }
            });
            var docs = db.GetCollection("docs");
            docs.Insert(Enumerable.Range(1, 2).Select(id => new BsonDocument
            {
                ["_id"] = id,
                ["Embedding"] = new BsonVector(new[] { (float)id, 0f }),
                ["Owner"] = new BsonDocument { ["$id"] = id, ["$ref"] = "owners" }
            }));
            docs.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));

            var query = docs.Query().Include("$.Owner");
            if (filterIncluded) query.Where("$.Owner.Weight >= 10");
            if (topK) query.TopKNear("Embedding", new[] { 1f, 0f }, 2);
            else query.WhereNear("Embedding", new[] { 1f, 0f }, 2);

            var result = query.Select("{first: FIRST(*.Owner.Name), again: FIRST(*.Owner.Name), " +
                "total: SUM(*.Owner.Weight), repeatedTotal: SUM(*.Owner.Weight)}").Single();

            result["first"].AsString.Should().Be("first");
            result["again"].AsString.Should().Be("first");
            result["total"].AsInt32.Should().Be(30);
            result["repeatedTotal"].AsInt32.Should().Be(30);
        }
    }
}
