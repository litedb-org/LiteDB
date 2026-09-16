using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class BooleanPredicateOptimization_Tests
    {
        [Fact]
        public void Combined_linq_contains_exposes_the_intersection_without_changing_the_template()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection<RangeOptimization_Tests.Row>("rows");
            var keys = new[] { 1, 3, 5, 7, 9 };
            var minimum = 5;
            var template = db.Mapper.GetExpression<RangeOptimization_Tests.Row, bool>(x => keys.Contains(x.Score) && x.Score >= minimum);
            var source = template.Source;
            var query = rows.Query().Where(template).OrderByDescending(x => x.Score).Offset(1).Limit(2);
            query.GetPlan().ContainsKey("filters").Should().BeFalse();
            query.ToArray().Select(x => x.Score).Should().Equal(7, 5);
            template.Source.Should().Be(source);
            keys[4] = 10;
            minimum = 8;
            rows.Query().Where(x => keys.Contains(x.Score) && x.Score >= minimum).ToArray()
                .Select(x => x.Score).Should().Equal(10);
        }

        [Theory]
        [InlineData("(Score = 3) = true", new[] { 3 })]
        [InlineData("true = (Score = 3)", new[] { 3 })]
        [InlineData("(Score = 3) != false", new[] { 3 })]
        [InlineData("false != (Score = 3)", new[] { 3 })]
        [InlineData("(((Score = 3) = true) = true)", new[] { 3 })]
        [InlineData("(Score = 3 OR Score = 7) = true", new[] { 3, 7 })]
        [InlineData("(Score BETWEEN 1 AND 9) = true AND Score > 7", new[] { 8, 9 })]
        [InlineData("([1,3,5,7,9] ANY = Score) = true AND Score >= 7", new[] { 7, 9 })]
        public void Boolean_identity_comparisons_expose_shared_index_predicates(string predicate, int[] expected)
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("Score");
            query.GetPlan().ContainsKey("filters").Should().BeFalse();
            query.ToArray().Select(x => x["Score"].AsInt32).Should().Equal(expected);
        }

        [Fact]
        public void Bound_boolean_values_are_rechecked_and_negations_retain_their_semantics()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var template = BsonExpression.Create("@enabled = (Score = 3)", new BsonDocument { ["enabled"] = true });
            rows.Query().Where(template).GetPlan()["index"]["name"].AsString.Should().Be("Score");
            rows.Query().Where(template).Count().Should().Be(1);
            rows.Query().Where(template.Bind(new BsonDocument { ["enabled"] = false })).Count().Should().Be(9);
            rows.Query().Where(template.Bind(new BsonDocument { ["enabled"] = BsonValue.Null })).Count().Should().Be(0);
        }

        [Fact]
        public void Plain_field_comparisons_keep_bson_type_semantics()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            var values = new BsonValue[] { true, false, BsonValue.Null, 1, "true" };
            rows.InsertBulk(values.Select((x, i) => new BsonDocument { ["_id"] = i + 1, ["Value"] = x }));
            foreach (var predicate in new[] { "Value = true", "Value != false", "(Value = true) = false", "(Value != false) = true" })
            {
                var expr = BsonExpression.Create(predicate);
                var expected = rows.FindAll().Where(x => expr.ExecuteScalar(x).AsBoolean).Select(x => x["_id"].AsInt32);
                rows.Query().Where(expr).ToArray().Select(x => x["_id"].AsInt32).Should().Equal(expected);
            }
        }

        [Theory]
        [InlineData("(Values[*] ANY = 1) = true", 2)]
        [InlineData("(Values[*] ALL = 1) = true", 1)]
        public void Array_predicates_keep_any_and_all_semantics(string predicate, int expected)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(1, 9) });
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(1, 1) });
            rows.EnsureIndex("values", "Values[*]");
            rows.Query().Where(predicate).Count().Should().Be(expected);
        }

        [Fact]
        public void Unwrapping_does_not_evaluate_unreachable_expressions()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.Query().Where("(false AND (SUBSTRING(Name, 1000) = 'x')) = true").Count().Should().Be(0);
            Action execute = () => rows.Query().Where("((SUBSTRING(Name, 1000) = 'x') AND false) = true").Count();
            execute.Should().Throw<ArgumentOutOfRangeException>();
        }

        private static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i, ["Name"] = "row" }));
            rows.EnsureIndex("Score", "Score");
            return db;
        }
    }
}
