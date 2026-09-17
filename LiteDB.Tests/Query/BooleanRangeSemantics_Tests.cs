using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class BooleanRangeSemantics_Tests
    {
        [Theory]
        [InlineData("en-US/IgnoreCase")]
        [InlineData("en-US/None")]
        public void Randomized_nested_boolean_sets_match_scalar_evaluation_and_order(string culture)
        {
            var collation = new Collation(culture);
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation });
            var rows = db.GetCollection("rows");
            var values = new BsonValue[] { BsonValue.Null, -1, 0, 0L, 0.0, 0m, 1, "a", "A", "b", "B", "c", new BsonArray(1, 2), new BsonArray(2, 3) };
            var bounds = values.Concat(new[] { BsonValue.MinValue, BsonValue.MaxValue }).ToArray();
            rows.InsertBulk(Enumerable.Range(1, 200).Select(i => new BsonDocument { ["_id"] = i, ["Value"] = values[i % values.Length] }));
            var documents = rows.FindAll().ToArray();
            rows.EnsureIndex("value", "Value");
            var random = new Random(430);
            for (var round = 0; round < 100; round++)
            {
                var parameters = new BsonDocument();
                var leaves = Enumerable.Range(0, 8).Select(i =>
                {
                    parameters["v" + i] = bounds[random.Next(bounds.Length)];
                    parameters["w" + i] = bounds[random.Next(bounds.Length)];
                    parameters["s" + i] = new BsonArray(Enumerable.Range(0, random.Next(7)).Select(_ => bounds[random.Next(bounds.Length)]));
                    var operation = random.Next(7);
                    return operation == 0 ? "Value IN @s" + i : operation == 1 ? "Value BETWEEN @v" + i + " AND @w" + i :
                        "Value " + new[] { "=", ">", ">=", "<", "<=" }[operation - 2] + " @v" + i;
                }).ToArray();
                var source = "((" + leaves[0] + " OR " + leaves[1] + ") AND (" + leaves[2] + " OR " + leaves[3] + ")) OR " +
                    "((" + leaves[4] + " OR " + leaves[5] + ") AND (" + leaves[6] + " OR " + leaves[7] + "))";
                var predicate = BsonExpression.Create(source, parameters);
                var expected = documents.Where(x => predicate.ExecuteScalar(x, collation).AsBoolean).Select(x => x["_id"]).ToArray();
                foreach (var order in new[] { Query.Ascending, Query.Descending })
                {
                    var query = rows.Query().Where(predicate).OrderBy("Value", order);
                    query.GetPlan().ContainsKey("filters").Should().BeFalse();
                    var results = query.ToArray();
                    results.Select(x => x["_id"]).Should().BeEquivalentTo(expected);
                    for (var i = 1; i < results.Length; i++)
                        (results[i - 1]["Value"].CompareTo(results[i]["Value"], collation) * order).Should().BeLessThanOrEqualTo(0);
                }
            }
        }

        [Theory]
        [InlineData(8, false)]
        [InlineData(20, true)]
        public void Alternating_boolean_shapes_are_bounded_without_distributing_or_arms(int count, bool fallback)
        {
            using var db = BooleanRangeOptimization_Tests.CreateDatabase();
            var rows = db.GetCollection("rows");
            var groups = Enumerable.Range(0, count).Select(i => "(Score < " + (i + 2) + " OR Score > " + (i + 4) + ")");
            var expression = BsonExpression.Create("(" + string.Join(" AND ", groups) + ") OR Score = 5");
            var expected = rows.FindAll().Where(x => expression.ExecuteScalar(x).AsBoolean).Select(x => x["_id"]);
            var query = rows.Query().Where(expression);
            query.GetPlan().ContainsKey("filters").Should().Be(fallback);
            query.ToArray().Select(x => x["_id"]).Should().BeEquivalentTo(expected);
        }

        [Theory]
        [InlineData("(Score >= 1 AND (Score < 5 OR RANDOM() > 0)) OR Score > 18")]
        [InlineData("(Score >= 1 AND (Score < 5 OR Name = 'even')) OR Score > 18")]
        [InlineData("(Score >= 1 AND (Score < 5 OR ABS(Score) > 18)) OR Score > 18")]
        [InlineData("(Values[*] ANY > 8 AND (Values[*] ANY < 3 OR Values[*] ANY = 5)) OR Values[*] ANY = 9")]
        [InlineData("(Values[*] ALL > 8 AND (Values[*] ALL < 3 OR Values[*] ALL = 5)) OR Values[*] ALL = 9")]
        public void Unsupported_leaves_do_not_disappear_from_nested_filters(string source)
        {
            using var db = BooleanRangeOptimization_Tests.CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 21, ["Score"] = 21, ["Values"] = new BsonArray(1, 9) });
            rows.EnsureIndex("values", "Values[*]");
            rows.EnsureIndex("absolute", "ABS(Score)");
            var expression = BsonExpression.Create(source);
            rows.Query().Where(expression).GetPlan().ContainsKey("filters").Should().BeTrue();
            if (!source.Contains("RANDOM"))
            {
                var expected = rows.FindAll().Where(x => expression.ExecuteScalar(x).AsBoolean).Select(x => x["_id"]);
                rows.Find(expression).Select(x => x["_id"]).Should().BeEquivalentTo(expected);
            }
        }

        [Theory]
        [InlineData("(Score < @limit AND (Score > 0 OR SUBSTRING('x',1000) = 'z')) OR Score > (1 % @zero)")]
        [InlineData("(Score < @limit AND (Score > 0 OR Score IN [1 % @zero])) OR Score > (1 % @zero)")]
        public void Nested_short_circuits_and_execution_errors_remain_observable(string source)
        {
            using var db = BooleanRangeOptimization_Tests.CreateDatabase();
            var rows = db.GetCollection("rows");
            var template = BsonExpression.Create(source, new BsonDocument { ["limit"] = 100, ["zero"] = 0 });
            rows.Query().Where(template).GetPlan().ContainsKey("filters").Should().BeTrue();
            rows.Count(template).Should().Be(20);
            Action execute = () => rows.Count(template.Bind(new BsonDocument { ["limit"] = 0, ["zero"] = 0 }));
            execute.Should().Throw<Exception>();
            rows.DeleteAll();
            rows.Count(template.Bind(new BsonDocument { ["limit"] = 0, ["zero"] = 0 })).Should().Be(0);
        }

        [Fact]
        public void Includes_use_resolved_members_before_evaluating_nested_logic()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("owners").Insert(new BsonDocument { ["_id"] = 1, ["Score"] = 5 });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Owner"] = new BsonDocument { ["$id"] = 1, ["$ref"] = "owners" } });
            rows.EnsureIndex("owner", "Owner.Score");
            var query = rows.Query().Include("Owner").Where("(Owner.Score >= 3 AND (Owner.Score <= 5 OR Owner.Score >= 8)) OR Owner.Score = 9");
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.ToArray().Single()["Owner"]["Score"].AsInt32.Should().Be(5);
        }
    }
}
