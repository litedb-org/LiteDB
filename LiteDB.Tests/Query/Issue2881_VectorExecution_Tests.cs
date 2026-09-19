using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class Issue2881_VectorExecution_Tests
    {
        [Theory]
        [InlineData("VECTOR_SIM(Embedding)")]
        [InlineData("VECTOR_SIM(Embedding, [1, 0], [0, 1])")]
        public void Vector_function_rejects_invalid_argument_counts(string expression)
        {
            Action parse = () => BsonExpression.Create(expression);
            parse.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.UNEXPECTED_TOKEN);
        }

        [Fact]
        public void Sql_vector_sort_can_use_the_ordinary_filter_index()
        {
            using var db = new LiteDatabase(":memory:");
            var docs = db.GetCollection("docs");
            docs.Insert(new BsonDocument { ["_id"] = 1, ["Category"] = "wanted", ["Embedding"] = new BsonVector(new[] { 1f, 0f }) });
            docs.EnsureIndex("category", "$.Category");
            docs.EnsureIndex("vector", "$.Embedding", new VectorIndexOptions(2));
            using var plan = db.Execute("EXPLAIN SELECT $._id FROM docs WHERE Category = 'wanted' ORDER BY VECTOR_SIM(Embedding, [1.0, 0.0]) LIMIT 3");
            plan.ToArray().Single()["index"]["name"].AsString.Should().Be("category");
        }

        [Theory]
        [InlineData(VectorDistanceMetric.Euclidean, 1, 0.5)]
        [InlineData(VectorDistanceMetric.DotProduct, 3, 3.0)]
        public void Includes_preserve_metric_order_and_reported_scores(VectorDistanceMetric metric, int nearestId, double score)
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("owners").Insert(new BsonDocument { ["_id"] = 1, ["Name"] = "owner" });
            var docs = db.GetCollection("docs");
            docs.Insert(new[]
            {
                Doc(1, new[] { 1f, 0.5f }), Doc(2, new[] { 2f, 0f }), Doc(3, new[] { 3f, 0f })
            });
            docs.EnsureIndex("vector", "$.Embedding", new VectorIndexOptions(2, metric));
            var nearest = docs.Query().Include("$.Owner").TopKNearWithScore("Embedding", new[] { 1f, 0f }, 1).Single();
            nearest.Document["_id"].AsInt32.Should().Be(nearestId);
            nearest.Score.Should().Be(score);
            nearest.Metric.Should().Be(metric);
            nearest.Document["Owner"]["Name"].AsString.Should().Be("owner");
        }

        private static BsonDocument Doc(int id, float[] vector)
        {
            return new BsonDocument
            {
                ["_id"] = id, ["Embedding"] = new BsonVector(vector),
                ["Owner"] = new BsonDocument { ["$id"] = 1, ["$ref"] = "owners" }
            };
        }

        [Fact]
        public void Scored_bounded_api_queries_keep_ann_without_sort_spilling()
        {
            using var temporary = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:", TempStream = temporary });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var field = typeof(LiteEngine).GetField("_sortDisk", BindingFlags.Instance | BindingFlags.NonPublic);
            ((SortDisk)field.GetValue(engine)).Dispose();
            var header = (HeaderPage)typeof(LiteEngine).GetField("_header", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            field.SetValue(engine, new SortDisk(new StreamFactory(temporary, null, false), Constants.PAGE_SIZE, header.Pragmas));
            var docs = db.GetCollection("docs");
            docs.InsertBulk(Enumerable.Range(1, 800).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Embedding"] = new BsonVector(new[] { 1f, 0f })
            }));
            docs.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));
            var result = docs.Query().WhereNear("Embedding", new[] { 1f, 0f }, 1.2)
                .TopKNear("Embedding", new[] { 1f, 0f }, 1).WithScore().Single();
            result.Score.Should().Be(0);
            temporary.Length.Should().Be(0, "bounded ANN must not scan and sort the collection when scores are requested");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Exact_vector_queries_spill_sort_keys_and_reload_projected_scores(bool thresholdOnly)
        {
            using var temporary = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:", TempStream = temporary });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            // A small sort container exercises disk spilling with a modest deterministic fixture.
            var field = typeof(LiteEngine).GetField("_sortDisk", BindingFlags.Instance | BindingFlags.NonPublic);
            ((SortDisk)field.GetValue(engine)).Dispose();
            var header = (HeaderPage)typeof(LiteEngine).GetField("_header", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
            field.SetValue(engine, new SortDisk(new StreamFactory(temporary, null, false), Constants.PAGE_SIZE, header.Pragmas));
            var docs = db.GetCollection("docs");
            docs.InsertBulk(Enumerable.Range(1, 800).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Embedding"] = new BsonVector(new[] { (float)i, 0f }),
                ["Payload"] = new string('x', 4000)
            }));
            docs.EnsureIndex("vector", "$.Embedding", new VectorIndexOptions(2, VectorDistanceMetric.DotProduct));
            var query = docs.Query();
            if (thresholdOnly) query.WhereNear("Embedding", new[] { 1f, 0f }, 0);
            else query.Where(x => x["_id"] > 0).TopKNear("Embedding", new[] { 1f, 0f }, 3).Skip(1);
            var projected = query.Select(BsonExpression.Create("$._id"));
            var results = projected.WithScore().ToArray();
            results.Select(x => x.Document["_id"].AsInt32).Should().Equal(thresholdOnly
                ? Enumerable.Range(1, 800).Reverse() : new[] { 799, 798, 797 });
            results.Should().OnlyContain(x => x.Score == x.Document["_id"].AsInt32);
            results.Should().OnlyContain(x => !x.Document.ContainsKey("Payload"));
            temporary.Length.Should().BeGreaterThan(0, "exact vector ranking must use the bounded temporary sorter");
        }
    }
}
