using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class Issue2881_ManualVectorQuery_Tests
    {
        [Theory]
        [InlineData("Embedding[")]
        [InlineData("COALESCE(")]
        public void Unparseable_manual_vector_field_retains_ordinary_query_execution(string vectorField)
        {
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var docs = db.GetCollection("docs");
            docs.Insert(Enumerable.Range(1, 3).Select(id => new BsonDocument
            {
                ["_id"] = id, ["Category"] = id < 3 ? "keep" : "exclude",
                ["Embedding"] = new BsonVector(new[] { (float)id, 1f })
            }));
            docs.EnsureIndex("category", "$.Category");
            docs.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2));
            var query = new Query
            {
                VectorField = vectorField, VectorTarget = new[] { 1f, 0f }, Limit = 1
            };
            query.Where.Add(BsonExpression.Create("$.Category = 'keep'"));
            query.OrderBy.Add(new QueryOrder(BsonExpression.Create("VECTOR_SIM($.Embedding, [1,0])"), Query.Ascending));

            using (var reader = engine.Query("docs", query))
            {
                reader.ToArray().Single()["_id"].AsInt32.Should().Be(2);
            }
            query.ExplainPlan = true;
            using var plan = engine.Query("docs", query);
            plan.ToArray().Single()["index"]["name"].AsString.Should().Be("category");
        }
    }
}
