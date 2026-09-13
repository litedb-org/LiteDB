using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class Issue2881_VectorComposition_Tests
    {
        [Theory]
        [InlineData(VectorDistanceMetric.Euclidean, false, false)]
        [InlineData(VectorDistanceMetric.Euclidean, true, false)]
        [InlineData(VectorDistanceMetric.Euclidean, false, true)]
        [InlineData(VectorDistanceMetric.Euclidean, true, true)]
        [InlineData(VectorDistanceMetric.DotProduct, false, false)]
        [InlineData(VectorDistanceMetric.DotProduct, true, false)]
        [InlineData(VectorDistanceMetric.DotProduct, false, true)]
        [InlineData(VectorDistanceMetric.DotProduct, true, true)]
        public void Scores_preserve_metric_thresholds_and_snapshot_parity(VectorDistanceMetric metric, bool topK, bool project)
        {
            using var db = new LiteDatabase(":memory:");
            var docs = Populate(db, metric);
            var threshold = metric == VectorDistanceMetric.Euclidean ? 1.2 : -0.5;
            var query = docs.Query().WhereNear("Embedding", new[] { 1f, 0f }, threshold);
            if (topK) query.TopKNear("Embedding", new[] { 1f, 0f }, 4);
            var selected = project ? query.Select(BsonExpression.Create("$._id")) : query;
            var plain = selected.ToArray().Select(x => x["_id"].AsInt32).ToArray();
            plain.Should().Equal(metric == VectorDistanceMetric.Euclidean ? new[] { 1, 2 } : new[] { 4, 1, 2 });
            var snapshot = selected.WithScore();
            selected.Limit(1);
            var scored = snapshot.ToArray();
            scored.Select(x => x.Document["_id"].AsInt32).Should().Equal(plain);
            scored.Should().OnlyContain(x => x.Metric == metric);
            scored.Single(x => x.Document["_id"] == 2).Score.Should().BeApproximately(
                metric == VectorDistanceMetric.Euclidean ? 1.1 : -0.1, 1e-6);
            snapshot.Select(x => x.Document["_id"].AsInt32).Should().Equal(plain);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Computed_vector_expressions_keep_the_index_and_metric(bool topK, bool scored)
        {
            using var db = new LiteDatabase(":memory:");
            var docs = db.GetCollection("docs");
            docs.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(new[] { 1f, 0f }) },
                new BsonDocument { ["_id"] = 2, ["Embedding"] = new BsonVector(new[] { 10f, 0f }) },
                new BsonDocument { ["_id"] = 3 }
            });
            var expression = BsonExpression.Create("COALESCE($.Embedding, [0,1])");
            docs.EnsureIndex("computed", expression, new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));
            var query = docs.Query().WhereNear(expression, new[] { 1f, 0f }, 0.5);
            if (topK) query.TopKNear(expression, new[] { 1f, 0f }, 3);
            query.GetPlan()["index"]["name"].AsString.Should().Be("computed");
            if (scored)
            {
                var result = query.WithScore().Single();
                result.Document["_id"].AsInt32.Should().Be(1);
                result.Metric.Should().Be(VectorDistanceMetric.Euclidean);
                result.Score.Should().Be(0);
            }
            else query.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(1);
        }

        [Theory]
        [InlineData("repeat")]
        [InlineData("repeat-target")]
        [InlineData("topk-target")]
        [InlineData("topk-field")]
        [InlineData("where-after-topk")]
        public void Unsupported_api_combinations_fail_before_changing_the_query(string operation)
        {
            using var db = new LiteDatabase(":memory:");
            var docs = Populate(db, VectorDistanceMetric.Euclidean);
            var query = docs.Query();
            if (operation == "where-after-topk") query.TopKNear("Embedding", new[] { 1f, 0f }, 2);
            else query.WhereNear("Embedding", new[] { 1f, 0f }, 1.2);
            var before = query.ToArray().Select(x => x["_id"].AsInt32).ToArray();
            Action append = () =>
            {
                if (operation == "repeat") query.WhereNear("Embedding", new[] { 1f, 0f }, 0.5);
                else if (operation == "repeat-target" || operation == "where-after-topk") query.WhereNear("Embedding", new[] { 0f, 1f }, 0.5);
                else if (operation == "topk-target") query.TopKNear("Embedding", new[] { 0f, 1f }, 2);
                else query.TopKNear("Other", new[] { 1f, 0f }, 2);
            };
            append.Should().Throw<InvalidOperationException>();
            query.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(before);
        }

        private static ILiteCollection<BsonDocument> Populate(LiteDatabase db, VectorDistanceMetric metric)
        {
            var docs = db.GetCollection("docs");
            var values = new[] { 1f, -0.1f, -10f, 10f };
            docs.InsertBulk(values.Select((value, index) => new BsonDocument
            {
                ["_id"] = index + 1, ["Embedding"] = new BsonVector(new[] { value, 0f })
            }));
            docs.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2, metric));
            return docs;
        }
    }
}
