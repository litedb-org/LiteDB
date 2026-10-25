using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class RangeUnionCollation_Tests
    {
        [Theory]
        [InlineData("(Value >= 'a' AND Value < 'b') OR (Value >= 'B' AND Value <= 'c')")]
        [InlineData("(Value >= 'a' AND Value < 'b') OR (Value > 'B' AND Value <= 'c')")]
        [InlineData("(Value > 'a' AND Value <= 'b') OR (Value >= 'A' AND Value < 'b')")]
        [InlineData("Value < 'b' OR Value > 'B' OR Value = 'b'")]
        public void Boundary_merging_and_holes_use_the_active_collation(string predicate)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation("en-US/IgnoreCase") });
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(0, 240).Select(i => new BsonDocument { ["_id"] = i + 1, ["Value"] = "aAbBcCdD"[i % 8].ToString() }));
            var expected = rows.Find(predicate).Select(x => x["_id"]).ToArray();
            rows.EnsureIndex("value", "Value");
            foreach (var order in new[] { Query.Ascending, Query.Descending })
            {
                var query = rows.Query().Where(predicate).OrderBy("Value", order);
                query.GetPlan().ContainsKey("filters").Should().BeFalse();
                query.ToArray().Select(x => x["_id"]).Should().BeEquivalentTo(expected);
                query.Count().Should().Be(expected.Length);
            }
        }

        [Fact]
        public void Mixed_bson_bounds_match_scalar_execution_with_null_and_sentinel_limits()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            var values = new BsonValue[] { BsonValue.Null, -1, 0, 0L, 0.0, 0m, 1, "x", "y", new BsonArray(1, 2), new BsonArray(2, 3) };
            rows.InsertBulk(values.Select((value, i) => new BsonDocument { ["_id"] = i + 1, ["Value"] = value }));
            var bounds = values.Concat(new[] { BsonValue.MinValue, BsonValue.MaxValue }).ToArray();
            var documents = rows.FindAll().ToArray();
            rows.EnsureIndex("value", "Value");
            foreach (var low in bounds)
            foreach (var high in bounds)
            {
                var predicate = BsonExpression.Create("(Value >= @low AND Value < @high) OR (Value > @high AND Value <= @low)",
                    new BsonDocument { ["low"] = low, ["high"] = high });
                var expected = documents.Where(x => predicate.ExecuteScalar(x).AsBoolean).Select(x => x["_id"]).ToArray();
                foreach (var order in new[] { Query.Ascending, Query.Descending })
                {
                    var query = rows.Query().Where(predicate).OrderBy("Value", order);
                    query.GetPlan().ContainsKey("filters").Should().BeFalse();
                    query.ToArray().Select(x => x["_id"]).Should().BeEquivalentTo(expected);
                }
            }
        }

        [Fact]
        public void Randomized_overlaps_and_open_endpoints_match_full_scalar_evaluation()
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation("en-US/IgnoreCase") });
            var collation = db.Collation;
            var rows = db.GetCollection("rows");
            var values = new BsonValue[] { BsonValue.Null, -1, 0, 0L, 0.0, 0m, 1, "a", "A", "b", "B", "c", new BsonArray(1, 2) };
            rows.InsertBulk(Enumerable.Range(1, 200).Select(i => new BsonDocument { ["_id"] = i, ["Value"] = values[i % values.Length] }));
            var documents = rows.FindAll().ToArray();
            rows.EnsureIndex("value", "Value");
            var random = new Random(410);
            for (var round = 0; round < 150; round++)
            {
                var parameters = new BsonDocument();
                var branches = Enumerable.Range(0, 4).Select(i =>
                {
                    parameters["low" + i] = values[random.Next(values.Length)];
                    parameters["high" + i] = values[random.Next(values.Length)];
                    return "(Value " + (random.Next(2) == 0 ? ">" : ">=") + " @low" + i +
                        " AND Value " + (random.Next(2) == 0 ? "<" : "<=") + " @high" + i + ")";
                }).ToArray();
                var predicate = BsonExpression.Create(string.Join(" OR ", branches), parameters);
                var expected = documents.Where(x => predicate.ExecuteScalar(x, collation).AsBoolean).ToArray();
                foreach (var order in new[] { Query.Ascending, Query.Descending })
                {
                    var query = rows.Query().Where(predicate).OrderBy("Value", order);
                    query.GetPlan().ContainsKey("filters").Should().BeFalse();
                    var result = query.ToArray();
                    Int32IdAssertions.BeEquivalentTo(result.Select(x => x["_id"]), expected.Select(x => x["_id"]));
                    for (var i = 1; i < result.Length; i++)
                        (result[i - 1]["Value"].CompareTo(result[i]["Value"], collation) * order).Should().BeLessThanOrEqualTo(0);
                }
            }
        }
    }
}
