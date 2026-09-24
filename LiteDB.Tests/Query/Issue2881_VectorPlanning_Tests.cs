using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class Issue2881_VectorPlanning_Tests
    {
        private static readonly BsonDocument Parameters = new BsonDocument
        {
            ["q"] = new BsonArray { 1.0, 0.0 }
        };

        private static LiteDatabase CreateDatabase(int count = 12, bool ties = false)
        {
            var db = new LiteDatabase(":memory:");
            var docs = db.GetCollection("docs");
            var reference = db.GetCollection("reference");
            for (var i = count; i >= 1; i--)
            {
                var angle = (ties ? (i - 1) / 2 : i - 1) * Math.PI / count;
                var doc = new BsonDocument
                {
                    ["_id"] = i,
                    ["Embedding"] = new BsonVector(new[] { (float)Math.Cos(angle), (float)Math.Sin(angle) }),
                    ["Category"] = i > count / 2 ? "wanted" : "other",
                    ["Rank"] = count - i
                };
                docs.Insert(doc);
                reference.Insert(doc);
            }
            docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2, VectorDistanceMetric.Cosine));
            return db;
        }

        private static BsonValue[] Read(LiteDatabase db, string sql)
        {
            using var reader = db.Execute(sql, Parameters);
            return reader.ToArray();
        }

        [Theory]
        [InlineData("SELECT $.Embedding FROM {0}")]
        [InlineData("SELECT $ FROM {0} WHERE $.Embedding = @q")]
        [InlineData("SELECT $ FROM {0} WHERE @q = $.Embedding")]
        [InlineData("SELECT $ FROM {0} ORDER BY $.Embedding")]
        [InlineData("SELECT {{ key: @key, n: COUNT(*) }} FROM {0} GROUP BY $.Embedding")]
        public void Ordinary_queries_exclude_vector_indexes(string sql)
        {
            using var db = CreateDatabase();
            var plan = Read(db, "EXPLAIN " + string.Format(sql, "docs")).Single();
            plan["index"]["name"].AsString.Should().Be("_id");
            Read(db, string.Format(sql, "docs")).Should().Equal(Read(db, string.Format(sql, "reference")));
        }

        [Theory]
        [InlineData("ORDER BY $.Embedding VECTOR_SIM @q LIMIT 3 OFFSET 3")]
        [InlineData("WHERE $.Category = 'wanted' ORDER BY $.Embedding VECTOR_SIM @q LIMIT 3")]
        [InlineData("ORDER BY $.Embedding VECTOR_SIM @q DESC LIMIT 3")]
        [InlineData("ORDER BY $.Rank, $.Embedding VECTOR_SIM @q LIMIT 3")]
        [InlineData("ORDER BY $.Embedding VECTOR_SIM @q DESC")]
        [InlineData("ORDER BY $.Embedding VECTOR_SIM @q LIMIT 3 OFFSET 10")]
        [InlineData("ORDER BY $.Embedding VECTOR_SIM @q LIMIT 3 OFFSET 2147483646")]
        [InlineData("ORDER BY VECTOR_SIM($.Embedding, @q) LIMIT 3 OFFSET 3")]
        [InlineData("WHERE $.Category = 'wanted' ORDER BY VECTOR_SIM($.Embedding, @q) LIMIT 3")]
        [InlineData("ORDER BY VECTOR_SIM($.Embedding, @q) DESC LIMIT 3")]
        [InlineData("ORDER BY $.Rank, VECTOR_SIM($.Embedding, @q) LIMIT 3")]
        [InlineData("ORDER BY VECTOR_SIM($.Embedding, @q) DESC")]
        [InlineData("ORDER BY VECTOR_SIM($.Embedding, @q) LIMIT 3 OFFSET 10")]
        [InlineData("ORDER BY VECTOR_SIM($.Embedding, @q) LIMIT 3 OFFSET 2147483646")]
        public void Pagination_filtering_and_sorting_match_cosine_reference(string suffix)
        {
            using var db = CreateDatabase();
            var expected = Read(db, "SELECT $ FROM reference " + suffix);
            Read(db, "SELECT $ FROM docs " + suffix).Should().Equal(expected);
        }

        [Theory]
        [InlineData(12)]
        [InlineData(80)]
        public void Residual_filters_and_unbounded_queries_are_not_limited_by_ann_candidate_count(int count)
        {
            using var db = CreateDatabase(count);
            foreach (var suffix in new[]
            {
                "ORDER BY $.Embedding VECTOR_SIM @q",
                "WHERE $.Category = 'wanted' ORDER BY $.Embedding VECTOR_SIM @q LIMIT 3"
            })
            {
                Read(db, "SELECT $ FROM docs " + suffix).Should().Equal(Read(db, "SELECT $ FROM reference " + suffix));
            }
        }

        [Theory]
        [InlineData("ASC")]
        [InlineData("DESC")]
        public void Equal_distances_preserve_secondary_sort_before_limit(string direction)
        {
            using var db = CreateDatabase(ties: true);
            var suffix = " ORDER BY $.Embedding VECTOR_SIM @q, $._id " + direction + " LIMIT 3";
            Read(db, "SELECT $ FROM docs" + suffix).Should().Equal(Read(db, "SELECT $ FROM reference" + suffix));
        }

        [Fact]
        public void Group_limit_is_applied_after_collecting_all_matching_documents()
        {
            using var db = CreateDatabase();
            const string sql = "SELECT {{ key: @key, n: COUNT(*) }} FROM {0} " +
                "WHERE $.Embedding VECTOR_SIM @q <= 2 GROUP BY $.Category LIMIT 1";
            Read(db, string.Format(sql, "docs")).Should().Equal(Read(db, string.Format(sql, "reference")));
        }

        [Theory]
        [InlineData("WHERE $.Embedding VECTOR_SIM @q < 1 ORDER BY $._id LIMIT 12")]
        [InlineData("WHERE 1 > $.Embedding VECTOR_SIM @q ORDER BY $._id LIMIT 12")]
        [InlineData("WHERE $.Embedding VECTOR_SIM @q <= 2 ORDER BY $.Embedding LIMIT 3")]
        [InlineData("WHERE $.Embedding VECTOR_SIM @q <= 2 ORDER BY $.Embedding VECTOR_SIM [0.0, 1.0] LIMIT 3")]
        [InlineData("WHERE $.Embedding VECTOR_SIM @q <= $.Rank ORDER BY $._id")]
        public void Vector_predicates_retain_strict_bounds_and_independent_ordering(string suffix)
        {
            using var db = CreateDatabase();
            Read(db, "SELECT $ FROM docs " + suffix).Should().Equal(Read(db, "SELECT $ FROM reference " + suffix));
        }

        [Fact]
        public void Sql_cosine_queries_do_not_use_a_different_index_metric()
        {
            using var db = CreateDatabase();
            var docs = db.GetCollection("docs");
            docs.DropIndex("embedding_idx");
            docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2, VectorDistanceMetric.DotProduct));
            const string suffix = " ORDER BY VECTOR_SIM($.Embedding, @q) LIMIT 3";
            Read(db, "SELECT $ FROM docs" + suffix).Should().Equal(Read(db, "SELECT $ FROM reference" + suffix));
        }

        [Fact]
        public void Cosine_reference_has_explicit_expected_order_and_distances()
        {
            using var db = CreateDatabase();
            var results = Read(db, "SELECT $ FROM reference ORDER BY VECTOR_SIM($.Embedding, @q)");
            results.Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(1, 12));
            foreach (var doc in results)
            {
                var vector = doc["Embedding"].AsVector;
                var distance = 1 - vector[0] / Math.Sqrt(vector[0] * (double)vector[0] + vector[1] * (double)vector[1]);
                BsonExpression.Create("VECTOR_SIM($.Embedding, @q)", Parameters)
                    .ExecuteScalar(doc.AsDocument).AsDouble.Should().BeApproximately(distance, 1e-12);
            }
        }
    }
}
