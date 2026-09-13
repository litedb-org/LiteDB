using System.Linq;
using FluentAssertions;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class Issue2881_QuotedVectorField_Tests
    {
        private const string Upper = "$.[\"Embedding-Value\"]";
        private const string Lower = "$.[\"embedding-value\"]";

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Quoted_field_case_preserves_index_metric(bool computed, bool topK)
        {
            using var db = new LiteDatabase(":memory:");
            var docs = Populate(db, Expression(Upper, computed));
            var query = docs.Query();
            var expression = Expression(Lower, computed);
            if (topK) query.TopKNear(expression, new[] { 1f, 0f }, 1);
            else query.WhereNear(expression, new[] { 1f, 0f }, 0.6);

            var results = query.WithScore().ToArray();
            results.Select(x => x.Document["_id"].AsInt32).Should().Equal(1);
            results.Single().Metric.Should().Be(VectorDistanceMetric.Euclidean);
            results.Single().Score.Should().Be(0.5);
            query.GetPlan()["index"]["name"].AsString.Should().Be("embedding");
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Quoted_field_case_preserves_composed_thresholds(bool computed, bool topKFirst)
        {
            using var db = new LiteDatabase(":memory:");
            var docs = Populate(db, Expression(Upper, computed));
            var query = docs.Query();
            if (topKFirst)
            {
                query.TopKNear(Expression(Upper, computed), new[] { 1f, 0f }, 2);
                query.WhereNear(Expression(Lower, computed), new[] { 1f, 0f }, 0.6);
            }
            else
            {
                query.WhereNear(Expression(Upper, computed), new[] { 1f, 0f }, 0.6);
                query.TopKNear(Expression(Lower, computed), new[] { 1f, 0f }, 2);
            }

            var results = query.WithScore().ToArray();
            results.Select(x => x.Document["_id"].AsInt32).Should().Equal(1);
            results.Single().Metric.Should().Be(VectorDistanceMetric.Euclidean);
        }

        private static BsonExpression Expression(string field, bool computed)
        {
            return BsonExpression.Create(computed ? $"COALESCE({field}, [0,1])" : field);
        }

        private static ILiteCollection<BsonDocument> Populate(LiteDatabase db, BsonExpression expression)
        {
            var docs = db.GetCollection("docs");
            docs.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["Embedding-Value"] = new BsonVector(new[] { 1f, 0.5f }) },
                new BsonDocument { ["_id"] = 2, ["Embedding-Value"] = new BsonVector(new[] { 10f, 0f }) }
            });
            docs.EnsureIndex("embedding", expression, new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));
            return docs;
        }
    }
}
