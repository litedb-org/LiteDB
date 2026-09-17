using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class BooleanRangeOptimization_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_normalized_contains_term_retains_its_structural_proof_and_own_binding(bool separate)
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection<RangeOptimization_Tests.Row>("rows");
            var keys = new[] { 1, 3, 5, 7, 9, 18, 19 };
            var cut = 5;
            var tail = 18;
            var query = separate ? rows.Query().Where(x => keys.Contains(x.Score)).Where(x => x.Score < cut || x.Score >= tail) :
                rows.Query().Where(x => keys.Contains(x.Score) && (x.Score < cut || x.Score >= tail));
            query.GetPlan().ContainsKey("filters").Should().BeFalse();
            query.ToArray().Select(x => x.Score).Should().Equal(1, 3, 18, 19);
            keys[0] = 4;
            rows.Query().Where(x => keys.Contains(x.Score)).Where(x => x.Score < cut || x.Score >= tail)
                .ToArray().Select(x => x.Score).Should().Equal(3, 4, 18, 19);
        }

        [Theory]
        [InlineData(Query.Ascending)]
        [InlineData(Query.Descending)]
        public void Nested_ordinary_linq_uses_current_bounds_and_global_order(int order)
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection<RangeOptimization_Tests.Row>("rows");
            for (var start = 2; start < 5; start++)
            {
                var query = rows.Query().Where(x => (x.Score >= start && x.Score <= 15 &&
                    (x.Score < start + 2 || x.Score >= 14)) || x.Score == 19).OrderBy("Score", order);
                var plan = query.GetPlan();
                plan["index"]["mode"].AsString.Should().Contain("RANGE UNION");
                plan.ContainsKey("filters").Should().BeFalse();
                plan.ContainsKey("orderBy").Should().BeFalse();
                var expected = new[] { start, start + 1, 14, 15, 19 };
                if (order == Query.Descending) expected = expected.Reverse().ToArray();
                query.ToArray().Select(x => x.Score).Should().Equal(expected);
                query.Offset(1).Limit(3).ToArray().Select(x => x.Score).Should().Equal(expected.Skip(1).Take(3));
            }
        }

        [Fact]
        public void Separate_where_bindings_intersect_without_a_weaker_range_replacing_them()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection<RangeOptimization_Tests.Row>("rows");
            var low = 3;
            var high = 17;
            var cut = 5;
            var tail = 15;
            var query = rows.Query().Where(x => x.Score >= low && x.Score <= high).Where(x => x.Score < cut || x.Score >= tail);
            query.GetPlan()["index"]["mode"].AsString.Should().Contain("RANGE UNION");
            query.GetPlan().ContainsKey("filters").Should().BeFalse();
            query.ToArray().Select(x => x.Score).Should().Equal(3, 4, 15, 16, 17);
            low = 4;
            high = 18;
            cut = 6;
            tail = 17;
            rows.Query().Where(x => x.Score >= low && x.Score <= high).Where(x => x.Score < cut || x.Score >= tail)
                .ToArray().Select(x => x.Score).Should().Equal(4, 5, 17, 18);
        }

        [Theory]
        [InlineData("(Score < 2 OR Score > 8) AND (Score > 0 OR Score < 10)")]
        [InlineData("(Score < 5 OR Score >= 15) AND (Score >= 3 OR Score = 1) AND Score < 18")]
        [InlineData("((Score <= 5 OR Score = 9) AND (Score >= 5 OR Score = 1)) OR Score = 10")]
        [InlineData("((Score < 5 OR Score = 9) AND (Score >= 5 OR Score = 1)) OR Score = 10")]
        [InlineData("((Score <= 5 OR Score = 9) AND (Score > 5 OR Score = 1)) OR Score = 10")]
        [InlineData("((Score > 3 AND (Score < 5 OR Score > 8)) AND Score < 10) OR Score = 1")]
        [InlineData("(Score IN [1,3,5,7,9] AND (Score > 7 OR Score BETWEEN 2 AND 4)) OR Score BETWEEN 15 AND 17")]
        [InlineData("((Score BETWEEN 1 AND 5 OR Score IN [9]) AND (Score IN [1,3,9] OR Score BETWEEN 4 AND 8)) OR Score = 10")]
        [InlineData("(Score < 5 OR Score > 8) AND Score >= 5 AND Score <= 8")]
        [InlineData("(Score < 5 OR Score > 8) AND (Score >= 5 OR Score < 0) AND Score <= 8 AND Score >= 0")]
        [InlineData("((Score < 5 OR Score > 8) AND (Score >= 5 AND Score <= 8)) OR (Score > 10 AND Score < 10)")]
        [InlineData("((Score < 5 OR Score >= 5) AND (Score < 8 OR Score >= 8)) OR Score = 10")]
        [InlineData("(score > 2 AND (SCORE < 5 OR Score > 8)) OR Score = 1")]
        public void Boolean_interval_composition_matches_unindexed_results(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 300).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i % 21 }));
            var expected = rows.Find(predicate).Select(x => x["_id"]).ToArray();
            rows.EnsureIndex("score", "Score");
            foreach (var order in new[] { Query.Ascending, Query.Descending })
            {
                var query = rows.Query().Where(predicate).OrderBy("Score", order);
                query.GetPlan().ContainsKey("filters").Should().BeFalse();
                var result = query.ToArray();
                result.Select(x => x["_id"]).Should().BeEquivalentTo(expected);
                for (var i = 1; i < result.Length; i++)
                    (result[i - 1]["Score"].CompareTo(result[i]["Score"]) * order).Should().BeLessThanOrEqualTo(0);
                query.Count().Should().Be(expected.Length);
            }
        }

        [Fact]
        public void Unselected_index_groups_retain_their_original_filters()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("name", "Name");
            var query = rows.Query().Where("Score >= 3 AND Score <= 17 AND (Score < 5 OR Score >= 15) AND (Name = 'even' OR Name = 'other')");
            // Two name seeks cost less than the combined score ranges. The score
            // bounds must not become a competing partial scan, or lose their filters.
            query.GetPlan()["index"]["name"].AsString.Should().Be("name");
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            query.ToArray().Select(x => x["Score"].AsInt32).Should().BeEquivalentTo(new[] { 4, 16 });
            var both = rows.Query().Where("Score >= 3 AND Score <= 17 AND (Score < 5 OR Score >= 15) AND " +
                "(Name = 'even' OR Name = 'other') AND (Name >= 'e' OR Name = 'none')");
            both.ToArray().Select(x => x["Score"].AsInt32).Should().BeEquivalentTo(new[] { 4, 16 });
            both.GetPlan().ContainsKey("filters").Should().BeTrue();
        }

        [Fact]
        public void Binding_and_live_index_changes_recompute_nested_ranges()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var template = BsonExpression.Create("(Score >= @low AND (Score < @cut OR Score >= @tail)) OR Score = @point",
                new BsonDocument { ["low"] = 3, ["cut"] = 5, ["tail"] = 18, ["point"] = 1 });
            for (var round = 0; round < 3; round++)
            {
                if (round == 1) rows.DropIndex("score");
                if (round == 2) rows.EnsureIndex("score", "Score");
                rows.Query().Where(template).GetPlan().ContainsKey("filters").Should().Be(round == 1);
                rows.Find(template).Select(x => x["Score"].AsInt32).Should().BeEquivalentTo(new[] { 1, 3, 4, 18, 19, 20 });
                var bound = template.Bind(new BsonDocument { ["low"] = 8, ["cut"] = 10, ["tail"] = 21, ["point"] = 5 });
                rows.Find(bound).Select(x => x["Score"].AsInt32).Should().BeEquivalentTo(new[] { 5, 8, 9 });
            }
            template.Parameters["low"].AsInt32.Should().Be(3);
        }

        internal static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 20).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i, ["Name"] = i % 2 == 0 ? "even" : "odd" }));
            rows.EnsureIndex("score", "Score");
            return db;
        }
    }
}
