using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class DisjunctionAnalysis_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Flat_empty_ranges_still_beat_a_primary_key_seek(bool include)
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query();
            if (include) query.Include("Ref");
            query.Where("_id = 1 AND SUBSTRING(Name,1000) = 'x' AND " +
                "((Score > 5 AND Score < 2) OR (Score > 6 AND Score <= 6))");
            query.GetPlan()["index"]["mode"].AsString.Should().Contain("EMPTY");
            query.ToArray().Should().BeEmpty();
        }

        [Theory]
        [InlineData("Name = 'row' AND (Code = 1 OR Code = 2)", "code", 2)]
        [InlineData("Name = 'row' AND ((Code = 1 AND Score >= 0) OR (Code = 1 AND Score < 0))", "code", 1)]
        [InlineData("_id = 1 AND ((Score >= 1 AND (Score < 5 OR Score > 8)) OR Score = 9)", "_id", 1)]
        public void Competing_equality_union_and_shared_guard_costs_keep_their_candidates(string predicate, string expectedIndex, int count)
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Include("Ref").Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be(expectedIndex);
            query.Count().Should().Be(count);
        }

        [Theory]
        [InlineData("Score = SUBSTRING('x',1000) OR Name = 'other'")]
        [InlineData("(Score > (1 % @zero) AND Score < 10) OR Name = 'other'")]
        [InlineData("Score > (1 % @zero) OR (Score < 10 AND Name = 'other')")]
        public void Rejected_shapes_do_not_evaluate_bound_values_during_planning(string predicate)
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Where(predicate, new BsonDocument { ["zero"] = 0 });
            Action plan = () => query.GetPlan();
            plan.Should().NotThrow();
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            Action execute = () => query.ToArray();
            execute.Should().Throw<Exception>();
        }

        [Fact]
        public void Accepted_equality_values_keep_their_planning_time_error()
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Where("Score = SUBSTRING('x',1000) OR Score = 2");
            Action plan = () => query.GetPlan();
            plan.Should().Throw<ArgumentOutOfRangeException>();
        }

        [Theory]
        [InlineData("(Score > 5 AND Score < 2) OR Score > (1 % @zero)")]
        [InlineData("(Score > 5 AND Score < 2) OR Score BETWEEN (1 % @zero) AND @bad")]
        public void Empty_early_branches_do_not_hide_invalid_later_bounds(string predicate)
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Where(predicate, new BsonDocument { ["zero"] = 0, ["bad"] = 17 });
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            Action execute = () => query.ToArray();
            execute.Should().Throw<Exception>();
        }

        [Fact]
        public void Equality_and_range_rebinding_keep_parameter_documents_independent()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var equalities = BsonExpression.Create("Score = @a OR @b = Score OR score = @a", new BsonDocument { ["a"] = 1, ["b"] = 3 });
            var ranges = BsonExpression.Create("(Score >= @low AND Score < @high) OR (Score IN @keys AND Score > @cut)",
                new BsonDocument { ["low"] = 1, ["high"] = 3, ["keys"] = new BsonArray(4, 5), ["cut"] = 4 });
            for (var round = 0; round < 3; round++)
            {
                rows.Query().Where(equalities).OrderBy("Score").ToArray().Select(x => x["Score"].AsInt32).Should().Equal(1, 3);
                rows.Query().Where(equalities.Bind(new BsonDocument { ["a"] = 2, ["b"] = 4 })).OrderBy("Score")
                    .ToArray().Select(x => x["Score"].AsInt32).Should().Equal(2, 4);
                rows.Query().Where(ranges).OrderBy("Score").ToArray().Select(x => x["Score"].AsInt32).Should().Equal(1, 2, 5);
                rows.Query().Where(ranges.Bind(new BsonDocument { ["low"] = 6, ["high"] = 8, ["keys"] = new BsonArray(8, 9), ["cut"] = 8 }))
                    .OrderBy("Score").ToArray().Select(x => x["Score"].AsInt32).Should().Equal(6, 7, 9);
            }
            equalities.Parameters["a"].AsInt32.Should().Be(1);
            ranges.Parameters["keys"].AsArray.ToArray().Should().Equal(new BsonValue[] { 4, 5 });
        }

        [Fact]
        public void Equality_cost_analysis_is_local_to_each_execution_and_current_includes()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var predicate = BsonExpression.Create("Ref.Score = 100 AND ((Score >= 1 AND (Score < 3 OR Score > 8)) OR Score = 5)");
            rows.EnsureIndex("reference", "Ref.Score");
            rows.Query().Where(predicate).GetPlan()["index"]["name"].AsString.Should().Be("reference");
            var included = rows.Query().Include("Ref").Where(predicate);
            included.GetPlan()["index"]["name"].AsString.Should().Be("score");
            included.ToArray().Select(x => x["Score"].AsInt32).Should().Equal(1, 2, 5, 9, 10);
            rows.DropIndex("reference");
            rows.Query().Where(predicate).GetPlan()["index"]["name"].AsString.Should().Be("score");
            rows.Query().Where(predicate).ToArray().Should().BeEmpty();
        }

        private static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            db.GetCollection("refs").Insert(new BsonDocument { ["_id"] = 1, ["Score"] = 100 });
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Score"] = i, ["Code"] = i, ["Name"] = "row", ["Ref"] = new BsonDocument { ["$id"] = 1, ["$ref"] = "refs" }
            }));
            rows.EnsureIndex("score", "Score");
            rows.EnsureIndex("code", "Code", true);
            rows.EnsureIndex("name", "Name");
            return db;
        }
    }
}
