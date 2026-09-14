using System;
using System.Globalization;
using System.IO;
using System.Linq;

using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Audit2026
{
    public class QueryAuditRegression_Tests
    {
        private sealed class VectorDocument
        {
            public int Id { get; set; }
            public float[] Embedding { get; set; }
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H18_indexed_not_equal_uses_database_collation()
        {
            using var stream = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = stream,
                Collation = new Collation(9, CompareOptions.IgnoreCase)
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var col = db.GetCollection("people");
            col.EnsureIndex("name", "$.name");
            col.Insert(new BsonDocument { ["name"] = "john" });
            col.Insert(new BsonDocument { ["name"] = "mary" });

            col.Find("$.name != 'JOHN'").Select(x => x["name"].AsString)
                .Should().Equal("mary");
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H20_relational_expression_uses_supplied_collation()
        {
            var collation = new Collation(9, CompareOptions.IgnoreCase);
            BsonExpression.Create("'a' > 'B'").ExecuteScalar(collation).AsBoolean.Should().BeFalse();
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H21_indexed_like_rejects_non_string_keys()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection("items");
            col.EnsureIndex("name", "$.name");
            col.Insert(new BsonDocument { ["_id"] = 1, ["name"] = "1a" });
            col.Insert(new BsonDocument { ["_id"] = 2, ["name"] = 123 });

            col.Find("$.name LIKE '1%'").Select(x => x["_id"].AsInt32)
                .Should().Equal(1);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H22_descending_indexed_like_returns_matching_rows()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection("items");
            col.EnsureIndex("name", "$.name");
            foreach (var value in new[] { "alpha", "amber", "beta" })
            {
                col.Insert(new BsonDocument { ["name"] = value });
            }

            col.Query().Where("$.name LIKE 'a%'").OrderBy("$.name", Query.Descending)
                .ToDocuments().Select(x => x["name"].AsString)
                .Should().Equal("amber", "alpha");
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H23_index_in_honors_order_by()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection("items");
            col.EnsureIndex("code", "$.code");
            foreach (var value in new[] { 1, 2, 3 })
            {
                col.Insert(new BsonDocument { ["code"] = value });
            }

            col.Query().Where("$.code IN [3,1,2]").OrderBy("$.code")
                .ToDocuments().Select(x => x["code"].AsInt32)
                .Should().Equal(1, 2, 3);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H24_right_hand_like_keeps_original_predicate_semantics()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection("patterns");
            col.EnsureIndex("pattern", "$.pattern");
            col.Insert(new BsonDocument { ["pattern"] = "a%" });
            col.Insert(new BsonDocument { ["pattern"] = "abc" });
            col.Insert(new BsonDocument { ["pattern"] = "xyz" });

            db.Execute("SELECT $ FROM patterns WHERE @0 LIKE $.pattern", "abc").ToArray()
                .Select((BsonValue x) => x["pattern"].AsString)
                .Should().BeEquivalentTo("a%", "abc");
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H26_dimension_mismatch_does_not_match_undefined_scores()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<VectorDocument>("vectors");
            col.Insert(new[]
            {
                new VectorDocument { Id = 1, Embedding = new[] { 1f, 0f, 0f } },
                new VectorDocument { Id = 2, Embedding = new[] { 0f, 1f, 0f } }
            });
            col.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(3));

            col.Query().WhereNear(x => x.Embedding, new[] { 1f, 0f }, 0.25)
                .ToArray().Should().BeEmpty();
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H27_top_k_fallback_excludes_undefined_scores()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<VectorDocument>("vectors");
            col.Insert(new[]
            {
                new VectorDocument { Id = 1, Embedding = new[] { 1f, 0f } },
                new VectorDocument { Id = 2, Embedding = null },
                new VectorDocument { Id = 3, Embedding = new[] { 0f, 0f } },
                new VectorDocument { Id = 4, Embedding = new[] { 0.99f, 0.01f } }
            });

            col.Query().TopKNear(x => x.Embedding, new[] { 1f, 0f }, 2)
                .ToArray().Select(x => x.Id).Should().Equal(1, 4);
        }

        [Fact]
        [Trait("Category", "AuditBehavior")]
        public void H73_indexed_like_with_null_is_false_not_exception()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection("items");
            col.EnsureIndex("name", "$.name");
            col.Insert(new BsonDocument { ["name"] = "abc" });

            Action query = () => col.Query().Where("$.name LIKE @0", BsonValue.Null).ToList();
            query.Should().NotThrow();
            col.Query().Where("$.name LIKE @0", BsonValue.Null).ToList().Should().BeEmpty();
        }
    }
}
