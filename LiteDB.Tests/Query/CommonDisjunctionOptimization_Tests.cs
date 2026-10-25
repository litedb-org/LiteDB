using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class CommonDisjunctionOptimization_Tests
    {
        [Fact]
        public void Ordinary_linq_uses_the_shared_leading_index_guard()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection<Row>("rows");
            var city = "even";
            var query = rows.Query().Where(x => (x.City == city && x.Score < 4) || (x.City == city && x.Score > 6))
                .OrderByDescending(x => x.Score).Offset(1).Limit(2);
            var plan = query.GetPlan();
            plan["index"]["name"].AsString.Should().Be("city");
            plan.ContainsKey("filters").Should().BeTrue();
            query.ToArray().Select(x => x.Score).Should().Equal(8, 2);
            city = "odd";
            rows.Query().Where(x => (x.City == city && x.Score < 4) || (x.City == city && x.Score > 6))
                .ToArray().Select(x => x.Score).Should().BeEquivalentTo(new[] { 1, 3, 7, 9 });
        }

        [Theory]
        [InlineData("(City = 'even' AND Score < 4) OR (City = 'even' AND Score > 6)")]
        [InlineData("('even' = City AND Score < 4) OR ('even' = City AND Score > 6)")]
        [InlineData("(City = 'even' AND Score = 2) OR (City = 'even' AND Score = 8) OR (City = 'even' AND Score = 10)")]
        public void Shared_sql_guards_keep_the_original_disjunction_filter(string predicate)
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var expression = BsonExpression.Create(predicate);
            var expected = rows.FindAll().Where(x => expression.ExecuteScalar(x).AsBoolean).Select(x => x["Score"].AsInt32);
            var query = rows.Query().Where(expression);
            query.GetPlan()["index"]["name"].AsString.Should().Be("city");
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            query.ToArray().Select(x => x["Score"].AsInt32).Should().BeEquivalentTo(expected);
        }

        [Fact]
        public void Equivalent_values_use_active_collation_and_are_rechecked_on_binding()
        {
            using var db = CreateDatabase(new Collation("en-US/IgnoreCase"));
            var rows = db.GetCollection("rows");
            var template = BsonExpression.Create("(City = @a AND Score < 4) OR (City = @b AND Score > 6)",
                new BsonDocument { ["a"] = "EVEN", ["b"] = "even" });
            rows.Query().Where(template).GetPlan()["index"]["name"].AsString.Should().Be("city");
            rows.Query().Where(template).Count().Should().Be(3);
            var different = template.Bind(new BsonDocument { ["a"] = "even", ["b"] = "odd" });
            rows.Query().Where(different).GetPlan()["index"]["name"].AsString.Should().Be("_id");
            rows.Query().Where(different).ToArray().Select(x => x["Score"].AsInt32).Should().BeEquivalentTo(new[] { 2, 7, 9 });
            template.Parameters["a"].AsString.Should().Be("EVEN");
        }

        [Theory]
        [InlineData("(SUBSTRING(City,1000) = 'x' AND City = 'missing') OR (City = 'missing' AND Score > 5)")]
        [InlineData("(RANDOM() > 0 AND City = 'missing') OR (City = 'missing' AND Score > 5)")]
        public void Later_guards_are_not_moved_ahead_of_other_expressions(string predicate)
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            if (predicate.Contains("SUBSTRING"))
            {
                Action execute = () => query.Count();
                execute.Should().Throw<ArgumentOutOfRangeException>();
            }
        }

        [Fact]
        public void Leading_guards_preserve_short_circuits_of_the_remaining_expressions()
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Where(
                "(City = 'missing' AND SUBSTRING(City,1000) = 'x') OR (City = 'missing' AND RANDOM() > 0)");
            query.GetPlan()["index"]["name"].AsString.Should().Be("city");
            query.Count().Should().Be(0);
        }

        [Fact]
        public void Included_fields_are_not_assumed_to_equal_their_stored_index_keys()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("owners").Insert(new BsonDocument { ["_id"] = 1, ["Name"] = "even" });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument
            {
                ["Score"] = 1, ["Owner"] = new BsonDocument { ["$id"] = 1, ["$ref"] = "owners" }
            });
            rows.EnsureIndex("owner_name", "Owner.Name");
            var query = rows.Query().Include("Owner").Where(
                "(Owner.Name = 'even' AND Score = 1) OR (Owner.Name = 'even' AND Score = 2)");
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.Count().Should().Be(1);
        }

        [Fact]
        public void Oversized_disjunctions_fall_back_without_changing_results()
        {
            using var db = CreateDatabase();
            var predicate = string.Join(" OR ", Enumerable.Range(1, 80).Select(i => "(City = 'even' AND Score = " + i + ")"));
            var query = db.GetCollection("rows").Query().Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.Count().Should().Be(5);
        }

        [Fact]
        public void Scalar_guards_do_not_replace_multikey_predicates()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(1, 9), ["Score"] = 1 });
            rows.EnsureIndex("values", "Values[*]");
            var query = rows.Query().Where("(Values[*] ANY = 1 AND Score = 1) OR (Values[*] ANY = 1 AND Score = 2)");
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.Count().Should().Be(1);
        }

        private static LiteDatabase CreateDatabase(Collation collation = null)
        {
            var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation ?? Collation.Binary });
            var rows = db.GetCollection<Row>("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new Row { Id = i, Score = i, City = i % 2 == 0 ? "even" : "odd" }));
            rows.EnsureIndex("city", "City");
            return db;
        }

        public class Row
        {
            public int Id { get; set; }
            public int Score { get; set; }
            public string City { get; set; }
        }
    }
}
