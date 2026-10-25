using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class SetUnionOptimization_Tests
    {
        [Theory]
        [InlineData(Query.Ascending)]
        [InlineData(Query.Descending)]
        public void Ordinary_contains_in_or_uses_current_arrays_and_arithmetic_bounds(int order)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<RangeOptimization_Tests.Row>("rows");
            rows.InsertBulk(Enumerable.Range(1, 20).Select(i => new RangeOptimization_Tests.Row { Id = i, Score = i }));
            rows.EnsureIndex(x => x.Score);
            var keys = new[] { 2, 2, 8, 18 };
            for (var start = 7; start < 10; start++)
            {
                keys[0] = start;
                var query = rows.Query().Where(x => keys.Contains(x.Score) || (x.Score >= start && x.Score < start + 3)).OrderBy("Score", order);
                query.GetPlan()["index"]["mode"].AsString.Should().Contain("RANGE UNION");
                query.GetPlan().ContainsKey("filters").Should().BeFalse();
                query.GetPlan().ContainsKey("orderBy").Should().BeFalse();
                var expected = keys.Concat(Enumerable.Range(start, 3)).Distinct().OrderBy(x => x).ToArray();
                if (order == Query.Descending) expected = expected.Reverse().ToArray();
                query.ToArray().Select(x => x.Score).Should().Equal(expected);
                query.Offset(1).Limit(3).ToArray().Select(x => x.Score).Should().Equal(expected.Skip(1).Take(3));
            }
        }

        [Theory]
        [InlineData("Score IN [2,2,3,8] OR Score > 7")]
        [InlineData("Score BETWEEN 2 AND 4 OR Score BETWEEN 4 AND 6")]
        [InlineData("(Score IN [1,2,3,4,8] AND Score BETWEEN 2 AND 5) OR Score IN [8,9]")]
        [InlineData("(Score IN [1,2,3,4,8] AND Score IN [2,4,8,9]) OR Score BETWEEN 3 AND 5")]
        [InlineData("Score IN [2,4] OR Score IN [4,6,8] OR Score = 8")]
        [InlineData("Score IN 2 OR Score BETWEEN 8 AND 9")]
        [InlineData("Score IN [] OR Score BETWEEN 9 AND 1")]
        [InlineData("(Score IN [1,2] AND Score > 7) OR Score IN [8,9]")]
        [InlineData("Score IN [5] OR Score < 5 OR Score > 5")]
        [InlineData("[2,4,8] ANY = Score OR Score BETWEEN 3 AND 5")]
        [InlineData("(Score IN [2,4,8] OR Score BETWEEN 3 AND 5) AND Score >= 4")]
        public void Membership_and_between_unions_match_unindexed_results_without_duplicates(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 300).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i % 11 }));
            var expected = rows.Find(predicate).Select(x => x["_id"]).ToArray();
            rows.EnsureIndex("score", "Score");
            foreach (var order in new[] { Query.Ascending, Query.Descending })
            {
                var query = rows.Query().Where(predicate).OrderBy("Score", order);
                if (!predicate.EndsWith("AND Score >= 4")) query.GetPlan().ContainsKey("filters").Should().BeFalse();
                var result = query.ToArray();
                result.Select(x => x["_id"]).Should().BeEquivalentTo(expected);
                for (var i = 1; i < result.Length; i++)
                    (result[i - 1]["Score"].CompareTo(result[i]["Score"]) * order).Should().BeLessThanOrEqualTo(0);
            }
            rows.Count(predicate).Should().Be(expected.Length);
        }

        [Fact]
        public void Rebinding_and_index_changes_rebuild_set_intersections()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i }));
            var template = BsonExpression.Create("(Score IN @keys AND Score BETWEEN @low AND @high) OR Score IN [@point, @point + 1]",
                new BsonDocument { ["keys"] = new BsonArray(1, 2, 3, 4), ["low"] = 2, ["high"] = 3, ["point"] = 8 });
            for (var round = 0; round < 3; round++)
            {
                if (round == 1) rows.EnsureIndex("score", "Score");
                if (round == 2) rows.DropIndex("score");
                rows.Query().Where(template).GetPlan().ContainsKey("filters").Should().Be(round != 1);
                rows.Find(template).Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 2, 3, 8, 9 });
                var bound = template.Bind(new BsonDocument { ["keys"] = new BsonArray(7, 8, 9), ["low"] = 8, ["high"] = 9, ["point"] = 5 });
                rows.Find(bound).Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 5, 6, 8, 9 });
                template.Parameters["point"].AsInt32.Should().Be(8);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Aggregates_projection_grouping_and_pagination_use_the_disjoint_stream(bool empty)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 40).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i % 10 }));
            rows.EnsureIndex("score", "Score");
            var predicate = empty ? "Score IN [] OR Score BETWEEN 8 AND 2" : "Score IN [1,2,8] OR Score BETWEEN 7 AND 8";
            var count = empty ? 0 : 16;
            var projection = rows.Query().Where(predicate).OrderBy("Score", Query.Descending).Select("Score");
            if (!empty) projection.GetPlan()["lookup"]["loader"].AsString.Should().Be("index");
            projection.ToArray().Length.Should().Be(count);
            projection.Offset(5).Limit(3).Count().Should().Be(empty ? 0 : 3);
            var aggregate = rows.Query().Where(predicate).Select("{ n: COUNT(*), present: ANY(*) }");
            aggregate.GetPlan()["lookup"]["loader"].AsString.Should().Be("none");
            var result = aggregate.ToDocuments().Single();
            result["n"].AsInt32.Should().Be(count);
            result["present"].AsBoolean.Should().Be(!empty);
            using var grouped = db.Execute("SELECT { key: @key, n: COUNT(*) } FROM rows WHERE " + predicate + " GROUP BY Score");
            var groups = grouped.ToEnumerable().ToArray();
            groups.Select(x => x["key"].AsInt32).Should().Equal(empty ? new int[0] : new[] { 1, 2, 7, 8 });
            groups.Select(x => x["n"].AsInt32).Should().Equal(Enumerable.Repeat(4, empty ? 0 : 4));
        }

        [Fact]
        public void Large_parameter_sets_remain_eligible_and_other_index_filters_are_preserved()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 100).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i }));
            rows.EnsureIndex("score", "Score");
            var parameters = new BsonDocument { ["keys"] = new BsonArray(Enumerable.Range(1, 10000).Select(i => new BsonValue(i))) };
            var union = rows.Query().Where("Score IN @keys OR Score BETWEEN 80 AND 90", parameters);
            union.GetPlan().ContainsKey("filters").Should().BeFalse();
            union.Count().Should().Be(100);
            var cheap = rows.Query().Where("_id = 20 AND (Score IN @keys OR Score BETWEEN 80 AND 90)", parameters);
            cheap.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            cheap.GetPlan().ContainsKey("filters").Should().BeTrue();
            cheap.ToArray().Single()["_id"].AsInt32.Should().Be(20);
        }
    }
}
