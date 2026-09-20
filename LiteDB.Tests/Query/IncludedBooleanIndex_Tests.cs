using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class IncludedBooleanIndex_Tests
    {
        [Theory]
        [InlineData("Ref", "Score")]
        [InlineData("Owner.Manager", "owner.score")]
        public void Unrelated_includes_keep_ordered_range_union_seeks(string include, string field)
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var predicate = $"({field} >= 2 AND {field} < 4) OR {field} = 8";
            var expected = rows.Query().Include(include).Where(predicate).ToArray().OrderBy(x => x["_id"]).ToArray();
            rows.EnsureIndex("key", field);
            var query = rows.Query().Include(include).Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("key");
            query.GetPlan().ContainsKey("filters").Should().BeFalse();
            query.ToArray().OrderBy(x => x["_id"]).Select(x => x.ToString()).Should().Equal(expected.Select(x => x.ToString()));
        }

        [Theory]
        [InlineData("(owner.score >= 2 AND (OWNER.SCORE < 4 OR owner.score >= 7)) AND owner.score <= 8")]
        [InlineData("(owner.score IN [2,3,8] AND (owner.score >= 3 OR owner.score < 0)) OR owner.score BETWEEN 5 AND 6")]
        [InlineData("(owner.score < 2 OR owner.score > 8) AND owner.score >= 2 AND owner.score <= 8")]
        [InlineData("(owner.score < 4 OR owner.score > 6) AND (owner.score >= 2 OR owner.score < 0) AND owner.score < 9")]
        public void Nested_boolean_sets_keep_included_values_order_and_pagination(string predicate)
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var expected = rows.Query().Include("Owner.Manager").Where(predicate).ToArray()
                .Select(x => x["Owner"]["Score"].AsInt32).OrderByDescending(x => x).ToArray();
            rows.EnsureIndex("key", "Owner.Score");
            var query = rows.Query().Include("Owner.Manager").Where(predicate).OrderBy("owner.score", Query.Descending);
            query.GetPlan().ContainsKey("filters").Should().BeFalse();
            var result = query.ToArray();
            result.Select(x => x["Owner"]["Score"].AsInt32).Should().Equal(expected);
            result.All(x => x["Owner"]["Manager"]["Score"].AsInt32 >= 101 && x["Owner"]["Manager"]["Score"].AsInt32 <= 104).Should().BeTrue();
            query.Offset(3).Limit(5).ToArray().Select(x => x["Owner"]["Score"].AsInt32).Should().Equal(expected.Skip(3).Take(5));
        }

        [Fact]
        public void Separate_where_bindings_are_intersected_without_changing_parameters()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("key", "Owner.Score");
            var first = BsonExpression.Create("owner.score >= @low AND owner.score <= @high", new BsonDocument { ["low"] = 2, ["high"] = 8 });
            var second = BsonExpression.Create("owner.score < @cut OR owner.score IN @keys", new BsonDocument { ["cut"] = 4, ["keys"] = new BsonArray(7, 8) });
            for (var low = 2; low < 5; low++)
            {
                var query = rows.Query().Include("Owner.Manager").Include("Ref")
                    .Where(first.Bind(new BsonDocument { ["low"] = low, ["high"] = 8 })).Where(second);
                query.GetPlan().ContainsKey("filters").Should().BeFalse();
                query.GetPlan()["index"]["name"].AsString.Should().Be("key");
                var result = query.ToArray();
                result.Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(Enumerable.Range(1, 40).Where(i =>
                    i % 10 >= low && i % 10 <= 8 && (i % 10 < 4 || i % 10 == 7 || i % 10 == 8)));
                result.Select(x => x["Ref"]["Name"].IsString && x["Owner"]["Manager"]["Name"].IsString).Should().OnlyContain(x => x);
            }
            first.Parameters["low"].AsInt32.Should().Be(2);
            second.Parameters["keys"].AsArray.ToArray().Should().Equal(new BsonValue[] { 7, 8 });
        }

        [Fact]
        public void Common_leading_guards_keep_residual_filters_on_resolved_members()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            const string predicate = "(Label = 'even' AND Ref.Score < 102) OR (Label = 'even' AND Ref.Score > 103)";
            var expected = rows.Query().Include("Ref").Where(predicate).ToArray().Select(x => x["_id"]).ToArray();
            expected.Should().NotBeEmpty();
            rows.EnsureIndex("label", "Label");
            var query = rows.Query().Include("Ref").Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("label");
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            query.ToArray().Select(x => x["_id"]).Should().BeEquivalentTo(expected);
        }

        [Fact]
        public void An_affected_equality_index_does_not_displace_safe_boolean_ranges()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            const string predicate = "((Score >= 2 AND Score < 4) OR Score = 8) AND Ref.Score = 101";
            var expected = rows.Query().Include("Ref").Where(predicate).ToArray().Select(x => x["_id"]).ToArray();
            rows.EnsureIndex("key", "Score");
            rows.EnsureIndex("affected", "Ref.Score");
            var query = rows.Query().Include("Ref").Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("key");
            query.ToArray().Select(x => x["_id"]).Should().BeEquivalentTo(expected);
        }

        [Fact]
        public void Ordinary_linq_combines_contains_ranges_and_dbref_includes()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection<Row>("rows");
            rows.EnsureIndex("key", "owner.score");
            var keys = new[] { 2, 3, 7, 8 };
            for (var low = 3; low < 5; low++)
            {
                var query = rows.Query().Include(x => x.Owner.Manager).Include(x => x.Ref)
                    .Where(x => (keys.Contains(x.Owner.Score) && x.Owner.Score >= low) || x.Owner.Score == 0)
                    .OrderByDescending(x => x.Owner.Score).Select(x => new { x.Id, x.Owner.Score, Manager = x.Owner.Manager.Name, Ref = x.Ref.Name });
                query.GetPlan()["index"]["name"].AsString.Should().Be("key");
                query.GetPlan().ContainsKey("filters").Should().BeFalse();
                query.GetPlan().ContainsKey("orderBy").Should().BeFalse();
                var result = query.ToArray();
                result.Select(x => x.Id).Should().BeEquivalentTo(Enumerable.Range(1, 40).Where(i =>
                    (keys.Contains(i % 10) && i % 10 >= low) || i % 10 == 0));
                result.Select(x => x.Manager == x.Ref && (x.Ref == "even" || x.Ref == "odd")).Should().OnlyContain(x => x);
                result.Select(x => x.Score).Should().BeInDescendingOrder();
            }
        }

        public class Row
        {
            public int Id { get; set; }
            public int Score { get; set; }
            public Owner Owner { get; set; }
            [BsonRef("owners")]
            public Person Ref { get; set; }
        }

        public class Owner
        {
            public int Score { get; set; }
            [BsonRef("owners")]
            public Person Manager { get; set; }
        }

        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        internal static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            db.GetCollection("owners").InsertBulk(Enumerable.Range(1, 4).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Score"] = 100 + i, ["Name"] = i % 2 == 0 ? "even" : "odd"
            }));
            db.GetCollection("rows").InsertBulk(Enumerable.Range(1, 40).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Score"] = i % 10, ["Label"] = i % 2 == 0 ? "even" : "odd",
                ["Ref"] = Reference(i), ["Owner"] = new BsonDocument { ["Score"] = i % 10, ["Manager"] = Reference(i) }
            }));
            return db;
        }

        internal static BsonDocument Reference(int id) => new BsonDocument { ["$id"] = id % 4 + 1, ["$ref"] = "owners" };
    }
}
