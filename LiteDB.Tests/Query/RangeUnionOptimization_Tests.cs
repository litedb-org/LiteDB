using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class RangeUnionOptimization_Tests
    {
        [Theory]
        [InlineData(Query.Ascending)]
        [InlineData(Query.Descending)]
        public void Ordinary_linq_ranges_seek_in_global_order_with_fresh_captures(int order)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<RangeOptimization_Tests.Row>("rows");
            rows.InsertBulk(Enumerable.Range(1, 20).Select(i => new RangeOptimization_Tests.Row { Id = i, Score = i }));
            rows.EnsureIndex(x => x.Score);
            for (var start = 2; start < 5; start++)
            {
                var query = rows.Query().Where(x => (x.Score >= start && x.Score < start + 3) ||
                    (x.Score >= 15 && x.Score < 18)).OrderBy("Score", order).Offset(2).Limit(3);
                var expected = Enumerable.Range(start, 3).Concat(Enumerable.Range(15, 3));
                if (order == Query.Descending) expected = expected.Reverse();
                var plan = query.GetPlan();
                plan["index"]["mode"].AsString.Should().Contain("RANGE UNION");
                plan.ContainsKey("filters").Should().BeFalse();
                plan.ContainsKey("orderBy").Should().BeFalse();
                query.ToArray().Select(x => x.Score).Should().Equal(expected.Skip(2).Take(3));
                query.Count().Should().Be(3);
                query.Exists().Should().BeTrue();
            }
        }

        [Theory]
        [InlineData("(Score >= 2 AND Score < 5) OR (Score >= 4 AND Score <= 7)")]
        [InlineData("(Score > 2 AND Score < 5) OR Score = 2")]
        [InlineData("Score = 2 OR Score > 3")]
        [InlineData("(Score >= 2 AND Score < 5) OR (Score > 5 AND Score <= 8)")]
        [InlineData("(Score >= 2 AND Score < 5) OR (Score >= 5 AND Score <= 8)")]
        [InlineData("(Score > 2 AND Score < 5) OR (Score >= 2 AND Score < 3)")]
        [InlineData("(Score >= 2 AND Score <= 8) OR (Score > 3 AND Score < 4)")]
        [InlineData("(Score > 8 AND Score < 2) OR Score = 5")]
        [InlineData("(Score > 8 AND Score < 2) OR (Score >= 5 AND Score < 5)")]
        [InlineData("Score < 5 OR Score >= 5")]
        [InlineData("(2 <= Score AND 5 > Score) OR (8 < Score AND 10 >= Score)")]
        [InlineData("(score >= 2 AND SCORE < 5) OR (Score > 8 AND Score <= 10)")]
        [InlineData("(Score >= 2 AND Score < 5) OR (Score > 5 AND Score <= 8) OR Score = 5")]
        public void Merged_ranges_match_unindexed_results_in_both_directions(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 300).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i % 12 }));
            var expected = rows.Find(predicate).Select(x => x["Score"].AsInt32).OrderBy(x => x).ToArray();
            rows.EnsureIndex("score", "Score");
            foreach (var order in new[] { Query.Ascending, Query.Descending })
            {
                var query = rows.Query().Where(predicate).OrderBy("Score", order);
                query.GetPlan().ContainsKey("filters").Should().BeFalse();
                var sorted = order == Query.Ascending ? expected : expected.Reverse().ToArray();
                query.ToArray().Select(x => x["Score"].AsInt32).Should().Equal(sorted);
                query.Offset(20).Limit(40).ToArray().Select(x => x["Score"].AsInt32).Should().Equal(sorted.Skip(20).Take(40));
            }
            rows.Count(predicate).Should().Be(expected.Length);
        }

        [Fact]
        public void A_point_bridging_open_ranges_produces_one_scan()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i }));
            rows.EnsureIndex("score", "Score");
            var query = rows.Query().Where("(Score >= 2 AND Score < 5) OR (Score > 5 AND Score <= 8) OR Score = 5");
            query.GetPlan()["index"]["mode"].AsString.Should().Contain("RANGE SCAN").And.NotContain("UNION");
            query.ToArray().Select(x => x["Score"].AsInt32).Should().Equal(Enumerable.Range(2, 7));
        }

        [Fact]
        public void Rebinding_and_live_catalog_changes_recompute_the_ranges()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i }));
            var template = BsonExpression.Create("(Score >= @low AND Score < @high) OR Score = @point",
                new BsonDocument { ["low"] = 2, ["high"] = 4, ["point"] = 8 });
            for (var round = 0; round < 3; round++)
            {
                if (round == 1) rows.EnsureIndex("score", "Score");
                if (round == 2) rows.DropIndex("score");
                var query = rows.Query().Where(template);
                query.GetPlan().ContainsKey("filters").Should().Be(round != 1);
                query.ToArray().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 2, 3, 8 });
                var bound = template.Bind(new BsonDocument { ["low"] = 8, ["high"] = 6, ["point"] = 5 });
                rows.Find(bound).Select(x => x["_id"].AsInt32).Should().Equal(5);
                template.Parameters["low"].AsInt32.Should().Be(2);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Projection_aggregates_grouping_and_secondary_sort_keep_their_results(bool empty)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 40).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i % 10 }));
            rows.EnsureIndex("score", "Score");
            var predicate = empty ? "(Score > 5 AND Score < 2) OR (Score > 6 AND Score <= 6)" : "Score < 2 OR Score > 7";
            var count = empty ? 0 : 16;
            var projection = rows.Query().Where(predicate).Select("Score");
            if (!empty) projection.GetPlan()["lookup"]["loader"].AsString.Should().Be("index");
            projection.ToArray().Length.Should().Be(count);
            var aggregate = rows.Query().Where(predicate).Select("{ n: COUNT(*), present: ANY(*) }");
            aggregate.GetPlan()["lookup"]["loader"].AsString.Should().Be("none");
            var result = aggregate.ToDocuments().Single();
            result["n"].AsInt32.Should().Be(count);
            result["present"].AsBoolean.Should().Be(!empty);
            using var grouped = db.Execute("SELECT { key: @key, n: COUNT(*) } FROM rows WHERE " + predicate + " GROUP BY Score");
            var groups = grouped.ToEnumerable().ToArray();
            groups.Select(x => x["key"].AsInt32).Should().Equal(empty ? new int[0] : new[] { 0, 1, 8, 9 });
            groups.Select(x => x["n"].AsInt32).Should().Equal(Enumerable.Repeat(4, empty ? 0 : 4));
            using var sorted = db.Execute("SELECT $ FROM rows WHERE " + predicate + " ORDER BY Score DESC, _id ASC LIMIT 7 OFFSET 3");
            sorted.ToEnumerable().Select(x => x["_id"].AsInt32).Should().Equal(empty ? new int[0] :
                Enumerable.Range(1, 40).Where(i => i % 10 < 2 || i % 10 > 7).OrderByDescending(i => i % 10).ThenBy(i => i).Skip(3).Take(7));
        }

        [Fact]
        public void A_cheaper_index_keeps_the_disjunction_as_a_filter()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i }));
            rows.EnsureIndex("score", "Score");
            var query = rows.Query().Where("(Score < 3 OR Score > 8) AND _id = 9");
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            query.ToArray().Single()["_id"].AsInt32.Should().Be(9);
            rows.Query().Where("(Score < 3 OR Score > 8) AND _id = 5").Count().Should().Be(0);
        }

        [Fact]
        public void Nested_members_handle_missing_fields_and_non_document_parents()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(new[] { new BsonDocument(), new BsonDocument { ["Info"] = 7 },
                new BsonDocument { ["Info"] = new BsonDocument { ["Score"] = 2 } },
                new BsonDocument { ["Info"] = new BsonDocument { ["Score"] = 8 } } });
            rows.EnsureIndex("nested", "Info.Score");
            var query = rows.Query().Where("Info.Score < 3 OR Info.Score > 6");
            query.GetPlan()["index"]["name"].AsString.Should().Be("nested");
            query.GetPlan().ContainsKey("filters").Should().BeFalse();
            query.Count().Should().Be(4);
        }
    }
}
