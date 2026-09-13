using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class Issue2881_UndefinedVector_Tests
    {
        public static IEnumerable<object[]> UndefinedOrderingCases()
        {
            foreach (var value in new BsonValue[]
            {
                null, BsonValue.Null, "not a vector", new BsonArray { 1 },
                new BsonArray { "invalid", 0 }, new BsonVector(new[] { 0f, 0f }),
                new BsonVector(new[] { float.NaN, 0f })
            })
            {
                foreach (var suffix in new[]
                {
                    "ORDER BY {0}", "ORDER BY {0} DESC", "ORDER BY {0} LIMIT 2",
                    "ORDER BY {0}, $._id DESC LIMIT 3 OFFSET 1",
                    "WHERE $._id > 1 ORDER BY {0} LIMIT 3",
                    "WHERE {0} <= 0.5 ORDER BY $._id",
                    "WHERE {0} < 0.5 ORDER BY $._id",
                    "WHERE {0} <= 2 GROUP BY $.Group ORDER BY $.Group"
                })
                {
                    yield return new object[] { value, suffix };
                }
            }
        }

        [Theory]
        [MemberData(nameof(UndefinedOrderingCases))]
        public void Sql_vector_queries_preserve_undefined_rows_and_null_ordering(BsonValue undefined, string suffix)
        {
            using var db = new LiteDatabase(":memory:");
            var documents = new[]
            {
                new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(new[] { 1f, 0f }), ["Group"] = 1 },
                new BsonDocument { ["_id"] = 2, ["Group"] = 2 },
                new BsonDocument { ["_id"] = 3, ["Embedding"] = new BsonVector(new[] { 1f, 1f }), ["Group"] = 1 },
                new BsonDocument { ["_id"] = 4, ["Embedding"] = new BsonVector(new[] { -1f, 0f }), ["Group"] = 2 }
            };
            if (undefined != null) documents[1]["Embedding"] = undefined;
            db.GetCollection("indexed").Insert(documents);
            db.GetCollection("reference").Insert(documents);
            db.GetCollection("indexed").EnsureIndex("vectors", "$.Embedding", new VectorIndexOptions(2));
            foreach (var expression in new[] { "VECTOR_SIM($.Embedding, [1.0, 0.0])", "$.Embedding VECTOR_SIM [1.0, 0.0]" })
            {
                var select = suffix.Contains("GROUP BY") ? "SELECT { Group: @key, n: COUNT(*) }" : "SELECT $";
                var ending = string.Format(suffix, expression);
                using var expected = db.Execute(select + " FROM reference " + ending);
                using var actual = db.Execute(select + " FROM indexed " + ending);
                actual.ToArray().Should().Equal(expected.ToArray());
            }
        }

        [Fact]
        public void Explicit_nearest_neighbor_search_with_scores_keeps_valid_hits()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection("docs");
            collection.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(new[] { 1f, 0f }) },
                new BsonDocument { ["_id"] = 2 },
                new BsonDocument { ["_id"] = 3, ["Embedding"] = new BsonVector(new[] { 0f, 0f }) }
            });
            collection.EnsureIndex("vectors", "$.Embedding", new VectorIndexOptions(2));
            var hits = collection.Query().Where(x => x["_id"] > 0)
                .TopKNearWithScore("Embedding", new[] { 1f, 0f }, 3).ToArray();
            hits.Should().ContainSingle();
            hits[0].Document["_id"].AsInt32.Should().Be(1);
            hits[0].Score.Should().Be(0);
        }
    }
}
