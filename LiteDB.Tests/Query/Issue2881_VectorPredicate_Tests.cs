using System.Linq;
using FluentAssertions;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class Issue2881_VectorPredicate_Tests
    {
        [Theory]
        [InlineData(VectorDistanceMetric.Cosine, "VECTOR_SIM(Embedding, [1,0]) <= 0.1")]
        [InlineData(VectorDistanceMetric.Euclidean, "VECTOR_SIM(Embedding, [1,0]) <= 0.1")]
        [InlineData(VectorDistanceMetric.Euclidean, "VECTOR_SIM(Embedding, [1,0]) < 0.1")]
        [InlineData(VectorDistanceMetric.Euclidean, "0.1 >= VECTOR_SIM(Embedding, [1,0])")]
        [InlineData(VectorDistanceMetric.DotProduct, "VECTOR_SIM(Embedding, [1,0]) <= 0.1")]
        public void Scalar_cosine_filter_does_not_replace_the_api_metric_threshold(VectorDistanceMetric metric, string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var docs = Populate(db, metric);
            var results = docs.Query().Where(predicate).TopKNear("Embedding", new[] { 1f, 0f }, 3).WithScore().ToArray();
            results.Select(x => x.Document["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 2, 3 });
            if (metric == VectorDistanceMetric.Euclidean) results.Select(x => x.Score).Should().Equal(0d, 1d, 2d);
            if (metric == VectorDistanceMetric.DotProduct) results.Select(x => x.Score).Should().Equal(3d, 2d, 1d);
        }

        [Theory]
        [InlineData(VectorDistanceMetric.Euclidean, 1.1, new[] { 1, 2 })]
        [InlineData(VectorDistanceMetric.DotProduct, 1.5, new[] { 2, 3 })]
        public void Scalar_filter_and_api_threshold_both_apply(VectorDistanceMetric metric, double threshold, int[] expected)
        {
            using var db = new LiteDatabase(":memory:");
            var docs = Populate(db, metric);
            var results = docs.Query().Where("VECTOR_SIM(Embedding, [1,0]) <= 0.1")
                .WhereNear("Embedding", new[] { 1f, 0f }, threshold).ToArray();
            results.Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(expected);
        }

        [Fact]
        public void Scalar_filter_on_another_field_does_not_choose_the_search_index()
        {
            using var db = new LiteDatabase(":memory:");
            var docs = Populate(db, VectorDistanceMetric.Euclidean);
            var query = docs.Query().Where("VECTOR_SIM(Other, [0,1]) <= 0.1").TopKNear("Embedding", new[] { 1f, 0f }, 3);
            query.GetPlan()["index"]["name"].AsString.Should().Be("embedding");
            query.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(1, 2, 3);
        }

        [Fact]
        public void Residual_cosine_filter_does_not_truncate_candidates_or_replace_the_api_target()
        {
            using var db = new LiteDatabase(":memory:");
            var docs = db.GetCollection("docs");
            docs.InsertBulk(Enumerable.Range(1, 40).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Embedding"] = new BsonVector(new[] { 1f, (float)i })
            }));
            docs.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));
            var results = docs.Query().Where("VECTOR_SIM(Embedding, [0,1]) <= 0.001")
                .TopKNear("Embedding", new[] { 1f, 0f }, 3).ToArray();
            results.Select(x => x["_id"].AsInt32).Should().Equal(23, 24, 25);
        }

        [Fact]
        public void Api_threshold_survives_a_matching_top_k_call()
        {
            using var db = new LiteDatabase(":memory:");
            var docs = Populate(db, VectorDistanceMetric.Euclidean);
            var results = docs.Query().WhereNear("Embedding", new[] { 1f, 0f }, 1.1)
                .TopKNear("Embedding", new[] { 1f, 0f }, 3).ToArray();
            results.Select(x => x["_id"].AsInt32).Should().Equal(1, 2);
        }

        private static ILiteCollection<BsonDocument> Populate(LiteDatabase db, VectorDistanceMetric metric)
        {
            var docs = db.GetCollection("docs");
            docs.InsertBulk(Enumerable.Range(1, 3).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Embedding"] = new BsonVector(new[] { (float)i, 0f }),
                ["Other"] = new BsonVector(new[] { 0f, 1f })
            }));
            docs.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2, metric));
            return docs;
        }
    }
}
