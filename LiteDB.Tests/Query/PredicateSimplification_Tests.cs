using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class PredicateSimplification_Tests
    {
        [Fact]
        public void Optional_linq_guard_exposes_the_index_and_is_rechecked_on_reuse()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection<RangeOptimization_Tests.Row>("rows");
            var enabled = true;
            var score = 3;
            var template = db.Mapper.GetExpression<RangeOptimization_Tests.Row, bool>(x => !enabled || x.Score == score);
            var query = rows.Query().Where(template);
            query.GetPlan()["index"]["name"].AsString.Should().Be("Score");
            query.ToArray().Select(x => x.Score).Should().Equal(3);
            var source = template.Source;
            rows.Query().Where(template.Bind(new BsonDocument { ["p0"] = false, ["p1"] = 3 })).Count().Should().Be(10);
            template.Source.Should().Be(source);
            enabled = false;
            rows.Query().Where(x => !enabled || x.Score == score).Count().Should().Be(10);
        }

        [Theory]
        [InlineData("(1 = 0 OR Score = 3) AND (2 = 2)", 1)]
        [InlineData("(1 = 1 AND Score = 3) OR (2 = 3)", 1)]
        [InlineData("(1 = 0 OR Score = 3) OR Score = 7", 2)]
        [InlineData("(1 = 0 OR Score = 3) OR (Name = 'x' AND 2 = 2)", 1)]
        public void Sql_constant_guards_simplify_shared_ir(string predicate, int count)
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Where(predicate);
            query.Count().Should().Be(count);
            if (!predicate.Contains("Name")) query.GetPlan()["index"]["name"].AsString.Should().Be("Score");
            else query.GetPlan().ContainsKey("filters").Should().BeTrue();
        }

        [Theory]
        [InlineData("true", 10)]
        [InlineData("false", 0)]
        [InlineData("false AND (SUBSTRING('a', 1000) = 'x')", 0)]
        [InlineData("true OR (SUBSTRING('a', 1000) = 'x')", 10)]
        public void Boolean_constants_and_short_circuits_preserve_results(string predicate, int count)
        {
            using var db = CreateDatabase();
            db.GetCollection("rows").Query().Where(predicate).Count().Should().Be(count);
        }

        [Theory]
        [InlineData("RANDOM() > 0")]
        [InlineData("IIF(RANDOM() > 0, true, false) = true")]
        [InlineData("IIF(@flag, true, false) = true")]
        public void Volatile_calls_and_parameter_dependent_conditionals_are_not_folded(string predicate)
        {
            using var db = CreateDatabase();
            var expression = BsonExpression.Create(predicate, new BsonDocument { ["flag"] = true });
            expression.IsImmutable.Should().BeFalse();
            db.GetCollection("rows").Query().Where(expression).GetPlan().ContainsKey("filters").Should().BeTrue();
        }

        [Fact]
        public void Right_absorbing_constants_do_not_suppress_evaluation_of_the_left()
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Where("SUBSTRING(Name, 1000) = 'x' AND false");
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            Action execute = () => query.Count();
            execute.Should().Throw<ArgumentOutOfRangeException>();
            Action separate = () => db.GetCollection("rows").Query()
                .Where("SUBSTRING(Name, 1000) = 'x'").Where("false").Count();
            separate.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData("false")]
        public void Empty_input_resets_vector_ordering_state(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Score"] = 5, ["Embedding"] = new BsonVector(new[] { 1f, 0f }) });
            rows.EnsureIndex("embedding", "Embedding", new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));
            var query = rows.Query().Where(predicate);
            query.TopKNear("Embedding", new[] { 1f, 0f }, 5);
            query.ThenBy("_id");
            query.GetPlan()["index"]["mode"].AsString.Should().StartWith("EMPTY");
            query.WithScore().ToArray().Should().BeEmpty();
        }

        [Fact]
        public void Residual_contradictions_return_no_vector_results()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Score"] = 5, ["Embedding"] = new BsonVector(new[] { 1f, 0f }) });
            rows.EnsureIndex("embedding", "Embedding", new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));
            var query = rows.Query().Where("Score > 10 AND Score < 1");
            query.TopKNear("Embedding", new[] { 1f, 0f }, 5);
            query.ThenBy("_id");
            query.WithScore().ToArray().Should().BeEmpty();
        }

        [Theory]
        [InlineData("@.Score = 3")]
        [InlineData("false OR @.Score = 3")]
        [InlineData("@.Score >= 3 AND @.Score <= 3")]
        public void Outer_current_paths_are_document_dependent(string predicate)
        {
            using var db = CreateDatabase();
            var expression = BsonExpression.Create(predicate);
            expression.IsValue.Should().BeFalse();
            db.GetCollection("rows").Query().Where(expression).ToArray()
                .Select(x => x["Score"].AsInt32).Should().Equal(3);
        }

        private static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i, ["Name"] = "none" }));
            rows.EnsureIndex("Score", "Score");
            return db;
        }
    }
}
