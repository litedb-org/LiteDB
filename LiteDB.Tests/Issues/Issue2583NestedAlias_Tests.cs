using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2583NestedAlias_Tests
    {
        [Theory]
        [InlineData("true")]
        [InlineData("false")]
        [InlineData("null")]
        public void Accepted_literal_word_alias_orders_its_projected_value(string alias)
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new[]
            {
                new BsonDocument { ["amount"] = 8 }, new BsonDocument { ["amount"] = 3 }
            });
            using var result = db.Execute("SELECT amount AS " + alias + " FROM rows ORDER BY " + alias);
            result.ToArray().Select(x => x[alias].AsInt32).Should().Equal(3, 8);
        }

        [Theory]
        [InlineData("{ score: 100-amount }")]
        [InlineData("EXTEND($, { score: 100-amount })")]
        [InlineData("$")]
        public void Single_document_alias_is_projected_and_used_for_ordering(string expression)
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new[]
            {
                new BsonDocument { ["_id"] = 2, ["amount"] = 3 },
                new BsonDocument { ["_id"] = 1, ["amount"] = 8 }
            });
            using var result = db.Execute("SELECT " + expression + " AS payload FROM rows ORDER BY payload");
            var values = result.ToArray();
            values.All(x => x["payload"].IsDocument).Should().BeTrue();
            if (expression == "$") values.Select(x => x["payload"]["_id"].AsInt32).Should().Equal(1, 2);
            else values.Select(x => x["payload"]["score"].AsInt32).Should().Equal(92, 97);
        }

        [Fact]
        public void Repeated_alias_renaming_does_not_overwrite_an_existing_suffix()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new[]
            {
                new BsonDocument { ["amount"] = 8 }, new BsonDocument { ["amount"] = 3 }
            });
            using var result = db.Execute("SELECT amount AS x, amount AS x1, 100-amount AS x FROM rows ORDER BY x1");
            var values = result.ToArray();
            values.Select(x => x["x1"].AsInt32).Should().Equal(3, 8);
            values.Select(x => x["x2"].AsInt32).Should().Equal(97, 92);
            values.All(x => x.AsDocument.Count == 3).Should().BeTrue();
        }

        [Fact]
        public void Case_variant_alias_names_are_unique_and_order_the_emitted_field()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new[]
            {
                new BsonDocument { ["amount"] = 8 }, new BsonDocument { ["amount"] = 3 }
            });
            using var result = db.Execute("SELECT 100-amount AS Rank, amount AS rank FROM rows ORDER BY Rank");
            var values = result.ToArray();
            values.Select(x => x["Rank"].AsInt32).Should().Equal(92, 97);
            values.Select(x => x["rank1"].AsInt32).Should().Equal(8, 3);
        }

        [Fact]
        public void Nested_alias_delegates_retain_each_queries_exact_literals()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["items"] = new BsonArray(1.0000000004) },
                new BsonDocument { ["_id"] = 2, ["items"] = new BsonArray(1.0000000006) }
            });
            foreach (var literal in new[] { "1.0000000004", "1.0000000006" })
            {
                using var result = db.Execute("SELECT _id, ARRAY(MAP(items[*] => ABS(@ - " + literal + "))) AS delta FROM rows ORDER BY delta");
                var rows = result.ToArray();
                rows.Select(x => x["_id"].AsInt32).Should().Equal(literal.EndsWith("4") ? new[] { 1, 2 } : new[] { 2, 1 });
                var actual = rows.Single(x => x["_id"].AsInt32 == 2)["delta"][0].AsDouble;
                var expected = System.Math.Abs(1.0000000006 - double.Parse(literal, System.Globalization.CultureInfo.InvariantCulture));
                actual.Should().Be(expected);
            }
        }
    }
}
