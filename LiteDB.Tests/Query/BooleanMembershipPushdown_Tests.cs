using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class BooleanMembershipPushdown_Tests
    {
        [Theory]
        [InlineData("Score IN @keys AND (Score < 12 OR Score >= 18) AND Score >= 10 AND Score <= 20")]
        [InlineData("(Score IN @keys OR Score = 50) AND (Score BETWEEN 10 AND 12 OR Score BETWEEN 18 AND 20)")]
        [InlineData("(Score BETWEEN 10 AND 12 OR Score BETWEEN 18 AND 20) AND (Score IN @keys OR Score = 50)")]
        [InlineData("Score >= 10 AND Score <= 20 AND (Score IN @keys OR Score = 50)")]
        [InlineData("(Score IN @keys OR Score = 50) AND Score >= 10 AND Score <= 20")]
        [InlineData("((Score IN @keys AND (Score < 12 OR Score >= 18)) OR Score = 50) AND Score >= 10 AND Score <= 20")]
        [InlineData("(Score IN @keys OR Score = 50) AND (Score < 10 OR Score > 20) AND Score >= 10 AND Score <= 20")]
        [InlineData("(Score IN @keys AND (Score < 10 OR Score >= 10)) OR Score = 50")]
        public void Parent_and_sibling_bounds_preserve_large_membership_results(string source)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 120).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i % 40 }));
            var documents = rows.FindAll().ToArray();
            rows.EnsureIndex("score", "Score");
            var keys = new BsonArray(Enumerable.Range(1, 10000).Reverse().Select(i => new BsonValue(i % 1000)));
            var original = keys.ToArray();
            var template = BsonExpression.Create(source, new BsonDocument { ["keys"] = keys });
            foreach (var order in new[] { Query.Ascending, Query.Descending })
            {
                var expected = documents.Where(x => template.ExecuteScalar(x).AsBoolean).Select(x => x["_id"]);
                var query = rows.Query().Where(template).OrderBy("Score", order);
                query.GetPlan().ContainsKey("filters").Should().BeFalse();
                query.GetPlan().ContainsKey("orderBy").Should().BeFalse();
                var result = query.ToArray();
                result.Select(x => x["_id"]).Should().BeEquivalentTo(expected);
                result.Select(x => x["Score"].AsInt32 * order).Should().BeInAscendingOrder();
                query.Offset(3).Limit(7).ToArray().Select(x => x["Score"]).Should().Equal(result.Skip(3).Take(7).Select(x => x["Score"]));
            }
            keys.ToArray().Should().Equal(original);
            keys.Clear();
            keys.Add(11);
            var rebound = template.Bind(new BsonDocument { ["keys"] = keys });
            rows.Find(rebound).Select(x => x["_id"]).Should().BeEquivalentTo(
                documents.Where(x => rebound.ExecuteScalar(x).AsBoolean).Select(x => x["_id"]));
        }

        [Fact]
        public void Separate_contains_bindings_intersect_before_expanding_their_points()
        {
            using var db = BooleanRangeOptimization_Tests.CreateDatabase();
            var rows = db.GetCollection<RangeOptimization_Tests.Row>("rows");
            var first = Enumerable.Range(1, 10000).ToArray();
            var second = Enumerable.Range(1, 5000).Select(i => i * 2).ToArray();
            var low = 5;
            var high = 18;
            var query = rows.Query().Where(x => first.Contains(x.Score)).Where(x => second.Contains(x.Score))
                .Where(x => x.Score < low || x.Score >= high);
            query.GetPlan().ContainsKey("filters").Should().BeFalse();
            query.ToArray().Select(x => x.Score).Should().Equal(2, 4, 18, 20);
            first[1] = 3;
            rows.Query().Where(x => first.Contains(x.Score)).Where(x => second.Contains(x.Score))
                .Where(x => x.Score < low || x.Score >= high).ToArray().Select(x => x.Score).Should().Equal(4, 18, 20);
        }

        [Theory]
        [InlineData("en-US/None")]
        [InlineData("en-US/IgnoreCase")]
        public void Interval_membership_uses_bson_order_and_current_collation(string culture)
        {
            var collation = new Collation(culture);
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation });
            var rows = db.GetCollection("rows");
            var values = new BsonValue[] { BsonValue.Null, -1, 0, 0L, 0m, 0.0, 1, "a", "A", "b", "B", "z", new byte[] { 1, 3 }, new BsonArray(1, 2) };
            rows.InsertBulk(Enumerable.Range(1, 140).Select(i => new BsonDocument { ["_id"] = i, ["Value"] = values[i % values.Length] }));
            var documents = rows.FindAll().ToArray();
            rows.EnsureIndex("value", "Value");
            var keys = new BsonArray(Enumerable.Range(0, 1000).Select(i => values[i % values.Length]));
            var source = "(Value IN @keys OR Value = 'absent') AND ((Value > 0 AND Value < 'b') OR Value = 'z')";
            var predicate = BsonExpression.Create(source, new BsonDocument { ["keys"] = keys });
            var query = rows.Query().Where(predicate);
            query.GetPlan().ContainsKey("filters").Should().BeFalse();
            query.ToArray().Select(x => x["_id"]).Should().BeEquivalentTo(
                documents.Where(x => predicate.ExecuteScalar(x, collation).AsBoolean).Select(x => x["_id"]));
        }

        [Fact]
        public void Reordered_bounds_preserve_skipped_and_throwing_membership_branches()
        {
            using var db = BooleanRangeOptimization_Tests.CreateDatabase();
            var rows = db.GetCollection("rows");
            const string source = "((Score IN @keys OR Score > (1 % @zero)) AND (Score < 1 OR Score > 20)) OR Score = 99";
            var parameters = new BsonDocument { ["keys"] = new BsonArray(Enumerable.Range(1, 20).Select(i => new BsonValue(i))), ["zero"] = 0 };
            var predicate = BsonExpression.Create(source, parameters);
            rows.Query().Where(predicate).GetPlan().ContainsKey("filters").Should().BeTrue();
            rows.Count(predicate).Should().Be(0);
            parameters["keys"] = new BsonArray();
            Action run = () => rows.Count(predicate);
            run.Should().Throw<Exception>();
            rows.DeleteAll();
            rows.Count(predicate).Should().Be(0);
        }

        [Theory]
        [InlineData("en-US/None", false)]
        [InlineData("en-US/None", true)]
        [InlineData("en-US/IgnoreCase", false)]
        [InlineData("en-US/IgnoreCase", true)]
        public void Dense_contexts_preserve_both_large_and_small_set_intersections(string culture, bool small)
        {
            var collation = new Collation(culture);
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation });
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(0, 200).Select(i => new BsonDocument { ["_id"] = i + 1, ["Value"] = "V" + i.ToString("D3") }));
            var documents = rows.FindAll().ToArray();
            rows.EnsureIndex("value", "Value");
            var context = new BsonArray(Enumerable.Range(0, 100).Select(i => new BsonValue("v" + (i * 2).ToString("D3"))));
            var keys = new BsonArray(small ? new BsonValue[] { "V100", "V195" } :
                Enumerable.Range(0, 200).Select(i => new BsonValue("V" + i.ToString("D3"))));
            var expression = BsonExpression.Create("(Value IN @context OR (Value > 'v190' AND Value < 'v195')) AND (Value IN @keys OR Value = 'absent')",
                new BsonDocument { ["context"] = context, ["keys"] = keys });
            var expected = documents.Where(x => expression.ExecuteScalar(x, collation).AsBoolean).Select(x => x["_id"]);
            foreach (var order in new[] { Query.Ascending, Query.Descending })
            {
                var query = rows.Query().Where(expression).OrderBy("Value", order);
                query.GetPlan().ContainsKey("filters").Should().BeFalse();
                var result = query.ToArray();
                result.Select(x => x["_id"]).Should().BeEquivalentTo(expected);
                for (var i = 1; i < result.Length; i++)
                    (result[i - 1]["Value"].CompareTo(result[i]["Value"], collation) * order).Should().BeLessThanOrEqualTo(0);
            }
        }

        [Fact]
        public void Membership_bounds_keep_key_moving_updates_distinct()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 200).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i % 20 }));
            rows.EnsureIndex("score", "Score");
            var predicate = BsonExpression.Create("(Score IN @keys AND (Score >= 3 OR Score = 1) AND Score <= 8) OR Score = 50",
                new BsonDocument { ["keys"] = new BsonArray(Enumerable.Range(0, 1000).Select(i => new BsonValue(i))) });
            db.BeginTrans();
            rows.UpdateMany("{ Score: Score + 3 }", predicate).Should().Be(70);
            rows.FindAll().OrderBy(x => x["_id"]).Select(x => x["Score"].AsInt32).Should().Equal(
                Enumerable.Range(1, 200).Select(i => i % 20).Select(score => score == 1 || (score >= 3 && score <= 8) ? score + 3 : score));
            db.Rollback();
            rows.Count(predicate).Should().Be(70);
        }
    }
}
