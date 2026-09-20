using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class ConstraintOptimization_Tests
    {
        [Theory]
        [InlineData("Score IN [1,3,5,7,9] AND Score >= 5 AND Score < 9", new[] { 5, 7 })]
        [InlineData("Score IN [1,3,5,7] AND Score IN [3,5,9]", new[] { 3, 5 })]
        [InlineData("Score BETWEEN 1 AND 9 AND Score BETWEEN 3 AND 5", new[] { 3, 4, 5 })]
        [InlineData("Score BETWEEN 1 AND 9 AND Score > 7", new[] { 8, 9 })]
        [InlineData("Score = 3 AND Score IN [1,3,5]", new[] { 3 })]
        [InlineData("Score IN [1,3] AND Score > 5", new int[0])]
        [InlineData("Score BETWEEN 9 AND 1 AND Score >= 1", new int[0])]
        public void Intersections_are_enforced_by_the_index(string predicate, int[] expected)
        {
            using var db = CreateDatabase();
            var query = db.GetCollection<Row>("rows").Query().Where(predicate).OrderByDescending(x => x.Score);
            var plan = query.GetPlan();
            plan.ContainsKey("filters").Should().BeFalse();
            query.ToArray().Select(x => x.Score).Should().Equal(expected.OrderByDescending(x => x));
        }

        [Fact]
        public void Linq_contains_and_separate_bindings_use_current_values()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection<Row>("rows");
            var ids = new[] { 1, 3, 5, 7 };
            var low = 5;
            var query = rows.Query().Where(x => ids.Contains(x.Score)).Where(x => x.Score >= low);
            query.GetPlan().ContainsKey("filters").Should().BeFalse();
            query.ToArray().Select(x => x.Score).Should().Equal(5, 7);
            ids[3] = 9;
            low = 6;
            rows.Query().Where(x => ids.Contains(x.Score) && x.Score >= low).ToArray().Select(x => x.Score).Should().Equal(9);
        }

        [Fact]
        public void Combined_cost_can_select_a_better_index_and_retains_other_filters()
        {
            using var db = CreateDatabase();
            var query = db.GetCollection<Row>("rows").Query().Where("City = 'same' AND Score IN [1,2,3,4,5] AND Score >= 5");
            var plan = query.GetPlan();
            plan["index"]["name"].AsString.Should().Be("Score");
            plan["index"]["mode"].AsString.Should().Contain("Score = 5");
            plan["filters"].AsArray.Count.Should().Be(1);
            query.ToArray().Select(x => x.Score).Should().Equal(5);
        }

        [Fact]
        public void Collation_nulls_and_numeric_types_match_direct_evaluation()
        {
            var collation = new Collation("en-US/IgnoreCase");
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation });
            var rows = db.GetCollection("rows");
            var values = new BsonValue[] { BsonValue.Null, 1, 1.0, 2L, "a", "A", "b", "C" };
            rows.InsertBulk(values.Select((v, i) => new BsonDocument { ["_id"] = i + 1, ["Value"] = v }));
            rows.EnsureIndex("value", "Value");
            foreach (var predicate in new[]
            {
                "Value IN [null,1,'a','C'] AND Value IN [1.0,'A','c']",
                "Value IN [null,1,'a','C'] AND Value >= 'A'",
                "Value BETWEEN null AND 'b' AND Value IN [null,1.0,'A','c']"
            })
            {
                var expression = BsonExpression.Create(predicate);
                var expected = rows.FindAll().Where(x => expression.ExecuteScalar(x, collation).AsBoolean).Select(x => x["_id"].AsInt32);
                rows.Query().Where(expression).ToArray().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(expected);
            }
        }

        [Fact]
        public void Randomized_scalar_domains_match_the_original_predicate()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection<Row>("rows");
            var random = new Random(734);
            for (var i = 0; i < 80; i++)
            {
                var parameters = new BsonDocument
                {
                    ["a"] = new BsonArray(Enumerable.Range(0, 8).Select(_ => new BsonValue(random.Next(1, 11)))),
                    ["b"] = new BsonArray(Enumerable.Range(0, 8).Select(_ => new BsonValue(random.Next(1, 11)))),
                    ["low"] = random.Next(0, 11), ["high"] = random.Next(0, 11)
                };
                var expression = BsonExpression.Create("Score IN @a AND Score IN @b AND Score BETWEEN @low AND @high", parameters);
                var expected = Enumerable.Range(1, 10).Where(x => expression.ExecuteScalar(new BsonDocument { ["Score"] = x }).AsBoolean);
                rows.Query().Where(expression).ToArray().Select(x => x.Score).Should().BeEquivalentTo(expected);
            }
        }

        [Theory]
        [InlineData("Values[*] ANY IN [1] AND Values[*] ANY > 5")]
        [InlineData("Values[*] ANY BETWEEN 1 AND 2 AND Values[*] ANY BETWEEN 8 AND 9")]
        public void Multikey_constraints_are_not_intersected(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(1, 9) });
            rows.EnsureIndex("values", "Values[*]");
            rows.Query().Where(predicate).Count().Should().Be(1);
        }

        [Theory]
        [InlineData("Score IN [RANDOM(1,10)] AND Score >= 1")]
        [InlineData("Score IN ARRAY(MAP([1] => RANDOM(1,10))) AND Score >= 1")]
        [InlineData("Score BETWEEN 1 AND RANDOM(2,10) AND Score >= 1")]
        public void Volatile_values_are_not_consumed_by_constraint_intersection(string predicate)
        {
            using var db = CreateDatabase();
            var expression = BsonExpression.Create(predicate);
            expression.IsVolatile.Should().BeTrue();
            expression.Bind(new BsonDocument()).IsVolatile.Should().BeTrue();
            db.GetCollection<Row>("rows").Query().Where(expression).GetPlan().ContainsKey("filters").Should().BeTrue();
        }

        [Theory]
        [InlineData(double.NaN, false)]
        [InlineData(double.NaN, true)]
        [InlineData(double.PositiveInfinity, false)]
        [InlineData(double.PositiveInfinity, true)]
        [InlineData(double.MaxValue, false)]
        [InlineData(double.MaxValue, true)]
        public void Bounds_that_fail_to_compare_leave_errors_to_the_original_filter(double bound, bool withRows)
        {
            // Intersecting the bounds compares them with each other. A scan only compares
            // them with stored values, and none of these rows reaches a numeric comparison.
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            if (withRows)
            {
                rows.Insert(new BsonDocument { ["_id"] = 1, ["Score"] = "text" });
                rows.Insert(new BsonDocument { ["_id"] = 2 });
            }
            rows.EnsureIndex("Score", "Score");
            var predicate = BsonExpression.Create("Score > @p AND Score < 100", new BsonDocument { ["p"] = bound });
            rows.Count(predicate).Should().Be(0);
        }

        private static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new Row { Id = i, Score = i, City = "same" }));
            rows.EnsureIndex(x => x.Score);
            rows.EnsureIndex(x => x.City);
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
