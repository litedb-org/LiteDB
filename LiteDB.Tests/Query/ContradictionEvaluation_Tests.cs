using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class ContradictionEvaluation_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Earlier_throwing_filters_remain_observable_before_contradictory_bounds(bool separateClauses)
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var query = separateClauses ? rows.Query().Where("SUBSTRING(Name,1000) = 'x'").Where("Score > 7").Where("Score < 3") :
                rows.Query().Where("SUBSTRING(Name,1000) = 'x' AND Score > 7 AND Score < 3");
            query.GetPlan()["index"]["mode"].AsString.Should().NotStartWith("EMPTY");
            Action execute = () => query.ToArray();
            execute.Should().Throw<ArgumentOutOfRangeException>();
            rows.DeleteAll();
            query.ToArray().Should().BeEmpty();
        }

        [Theory]
        [InlineData("Score > (1 % @zero) AND Score < 3")]
        [InlineData("Score = 0 AND Score > (1 % @zero)")]
        [InlineData("Score > (1 % 0) AND Score < 3")]
        [InlineData("Score = 0 AND Score > (1 % 0)")]
        public void Throwing_bounds_keep_empty_input_and_short_circuit_behavior(string source)
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var expression = BsonExpression.Create(source, new BsonDocument { ["zero"] = 0 });
            Action plan = () => rows.Query().Where(expression).GetPlan();
            plan.Should().NotThrow();
            if (source.StartsWith("Score = 0", StringComparison.Ordinal))
                rows.Count(expression).Should().Be(0);
            else
            {
                Action execute = () => rows.Count(expression);
                execute.Should().Throw<DivideByZeroException>();
            }
            rows.DeleteAll();
            rows.Count(expression).Should().Be(0);
        }

        [Fact]
        public void Reused_sql_retains_bound_errors()
        {
            using var db = CreateDatabase();
            var sql = "SELECT $ FROM rows WHERE Score > (1 % @zero) AND Score < 9";
            for (var attempt = 0; attempt < 4; attempt++)
            {
                using var reader = db.Execute(sql, new BsonDocument { ["zero"] = 2 });
                reader.ToArray().Should().HaveCount(1);
            }
            Action execute = () =>
            {
                using var reader = db.Execute(sql, new BsonDocument { ["zero"] = 0 });
                reader.ToArray();
            };
            execute.Should().Throw<DivideByZeroException>();
            var rows = db.GetCollection("rows");
            var safe = rows.Query().Where("Score > 7 AND Score < 3 AND SUBSTRING(Name,1000) = 'x'");
            safe.ToArray().Should().BeEmpty();
            rows.DeleteAll();
            execute.Should().NotThrow();
        }

        private static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["Score"] = 5, ["Name"] = "a" });
            return db;
        }
    }
}
