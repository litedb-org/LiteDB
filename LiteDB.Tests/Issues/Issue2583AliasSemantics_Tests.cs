using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2583AliasSemantics_Tests
    {
        [Fact]
        public void Inferred_projection_name_does_not_override_source_order()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new[]
            {
                new BsonDocument { ["amount"] = 8 }, new BsonDocument { ["amount"] = 3 }
            });
            using var result = db.Execute("SELECT 100 - amount FROM rows ORDER BY amount");
            result.ToArray().Select(x => x["amount"].AsInt32).Should().Equal(97, 92);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Alias_sort_retains_precision_of_parsed_double_constants(bool indexed)
        {
            using var db = new LiteDatabase(":memory:");
            if (indexed) db.GetCollection("rows").EnsureIndex("delta", "ABS(amount - 1.0)");
            db.GetCollection("rows").Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["amount"] = 1.0 },
                new BsonDocument { ["_id"] = 2, ["amount"] = 1.0000000005 }
            });
            using var result = db.Execute("SELECT _id, ABS(amount - 1.0000000004) AS precise FROM rows ORDER BY precise");
            if (indexed)
            {
                using var plan = db.Execute("EXPLAIN SELECT _id, ABS(amount - 1.0000000004) AS precise FROM rows ORDER BY precise");
                plan.ToArray().Single()["orderBy"].AsArray.Count.Should().Be(1);
            }
            var values = result.ToArray();
            values.Select(x => x["_id"].AsInt32).Should().Equal(2, 1);
            values.Select(x => x["precise"].AsDouble).Should().BeInAscendingOrder();
        }

        [Fact]
        public void Repeated_queries_keep_exact_alias_and_projection_constants_consistent()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["amount"] = 1.0000000004 },
                new BsonDocument { ["_id"] = 2, ["amount"] = 1.0000000006 }
            });
            foreach (var literal in new[] { "1.0000000004", "1.0000000006" })
            {
                using var result = db.Execute("SELECT _id, ABS(amount - " + literal + ") AS delta FROM rows ORDER BY delta");
                var rows = result.ToArray();
                rows.Select(x => x["_id"].AsInt32).Should().Equal(literal.EndsWith("4") ? new[] { 1, 2 } : new[] { 2, 1 });
                rows.Select(x => x["delta"].AsDouble).Should().BeInAscendingOrder();
            }
        }

        [Theory]
        [InlineData("amount")]
        [InlineData("rank")]
        public void Plain_indexed_alias_streams_index_without_external_sort(string alias)
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").EnsureIndex("amount");
            db.GetCollection("rows").Insert(new[]
            {
                new BsonDocument { ["amount"] = 8 }, new BsonDocument { ["amount"] = 3 }
            });
            using var result = db.Execute("EXPLAIN SELECT amount AS " + alias + " FROM rows ORDER BY " + alias);
            result.ToArray().Single()["orderBy"].IsNull.Should().BeTrue();
        }

        [Theory]
        [InlineData("GUID()")]
        [InlineData("ARRAY(MAP(items[*] => GUID()))")]
        public void Volatile_aliases_keep_existing_source_order_plan(string expression)
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new BsonDocument { ["rank"] = 1, ["items"] = new BsonArray(1, 2) });
            using var result = db.Execute("EXPLAIN SELECT " + expression + " AS rank FROM rows ORDER BY rank");
            result.ToArray().Single()["orderBy"][0]["expr"].AsString.Should().Be("$.rank");
        }
    }
}
