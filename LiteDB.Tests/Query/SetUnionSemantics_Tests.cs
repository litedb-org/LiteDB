using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class SetUnionSemantics_Tests
    {
        [Theory]
        [InlineData("Value IN @keys OR Value BETWEEN 8 AND 9", false)]
        [InlineData("@keys ANY = Value OR Value BETWEEN 8 AND 9", true)]
        public void Scalar_in_and_contains_preserve_binary_and_non_array_parameter_semantics(string text, bool items)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            var binary = new BsonValue(new byte[] { 1, 3 });
            rows.InsertBulk(new BsonValue[] { BsonValue.Null, 1, 3, 8, 9, binary }.Select((v, i) =>
                new BsonDocument { ["_id"] = i + 1, ["Value"] = v }));
            var documents = rows.FindAll().ToArray();
            rows.EnsureIndex("value", "Value");
            foreach (var keys in new[] { binary, BsonValue.Null, new BsonValue(1), new BsonArray(1, 3, 3), new BsonArray() })
            {
                var predicate = BsonExpression.Create(text, new BsonDocument { ["keys"] = keys });
                var expected = documents.Where(x => predicate.ExecuteScalar(x).AsBoolean).Select(x => x["_id"]).ToArray();
                var query = rows.Query().Where(predicate);
                query.GetPlan().ContainsKey("filters").Should().BeFalse();
                query.ToArray().Select(x => x["_id"]).Should().BeEquivalentTo(expected);
                if (ReferenceEquals(keys, binary)) expected.Length.Should().Be(items ? 4 : 3);
            }
        }

        [Fact]
        public void Randomized_set_and_range_intersections_follow_collation_and_bson_order()
        {
            var collation = new Collation("en-US/IgnoreCase");
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation });
            var rows = db.GetCollection("rows");
            var values = new BsonValue[] { BsonValue.Null, -1, 0, 0L, 0.0, 0m, 1, "a", "A", "b", "B", "c", new BsonArray(1, 2), new BsonArray(2, 3) };
            rows.InsertBulk(Enumerable.Range(1, 200).Select(i => new BsonDocument { ["_id"] = i, ["Value"] = values[i % values.Length] }));
            var documents = rows.FindAll().ToArray();
            rows.EnsureIndex("value", "Value");
            var random = new Random(420);
            var source = "(Value IN @a AND Value IN @b AND Value BETWEEN @low AND @high) OR " +
                "(Value IN [@point, @point] OR Value BETWEEN @start AND @end)";
            for (var round = 0; round < 100; round++)
            {
                var parameters = new BsonDocument
                {
                    ["a"] = new BsonArray(Enumerable.Range(0, random.Next(10)).Select(_ => values[random.Next(values.Length)])),
                    ["b"] = new BsonArray(Enumerable.Range(0, random.Next(10)).Select(_ => values[random.Next(values.Length)])),
                    ["low"] = values[random.Next(values.Length)], ["high"] = values[random.Next(values.Length)],
                    ["point"] = values[random.Next(values.Length)], ["start"] = values[random.Next(values.Length)], ["end"] = values[random.Next(values.Length)]
                };
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
        [InlineData("Score IN [RANDOM(1,10)] OR Score BETWEEN 8 AND 9")]
        [InlineData("Score BETWEEN 1 AND RANDOM(2,10) OR Score IN [8,9]")]
        [InlineData("Score IN ARRAY(MAP([1] => RANDOM(1,10))) OR Score IN [8,9]")]
        [InlineData("[2,3] ALL = Score OR Score BETWEEN 8 AND 9")]
        [InlineData("Values[*] ANY IN [1] OR Values[*] ANY BETWEEN 8 AND 9")]
        [InlineData("(Values[*] ANY IN [1] AND Values[*] ANY BETWEEN 8 AND 9) OR Values[*] ANY = 5")]
        [InlineData("Values[*] ALL IN [1] OR Values[*] ALL BETWEEN 8 AND 9")]
        [InlineData("Score IN [1,2] OR Other BETWEEN 8 AND 9")]
        [InlineData("Score IN [1,2] OR ABS(Score) BETWEEN 8 AND 9")]
        public void Volatile_multikey_mixed_and_computed_constraints_keep_their_filters(string predicate)
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.Query().Where(predicate).GetPlan().ContainsKey("filters").Should().BeTrue();
            if (!predicate.Contains("RANDOM"))
            {
                var expression = BsonExpression.Create(predicate);
                var expected = rows.FindAll().Where(x => expression.ExecuteScalar(x).AsBoolean).Select(x => x["_id"]);
                rows.Find(expression).Select(x => x["_id"]).Should().BeEquivalentTo(expected);
            }
        }

        [Theory]
        [InlineData("Score < @limit OR Score IN [SUBSTRING('x',1000)]")]
        [InlineData("Score < @limit OR Score BETWEEN 0 AND SUBSTRING('x',1000)")]
        [InlineData("Score < @limit OR [SUBSTRING('x',1000)] ANY = Score")]
        [InlineData("Score < @limit OR Score IN [1 % @zero]")]
        [InlineData("Score < @limit OR Score BETWEEN 0 AND (1 % @zero)")]
        public void Invalid_set_values_keep_short_circuits_and_execution_errors(string source)
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var template = BsonExpression.Create(source, new BsonDocument { ["limit"] = 100, ["zero"] = 0 });
            rows.Query().Where(template).GetPlan().ContainsKey("filters").Should().BeTrue();
            rows.Count(template).Should().Be(10);
            Action execute = () => rows.Count(template.Bind(new BsonDocument { ["limit"] = 0, ["zero"] = 0 }));
            execute.Should().Throw<Exception>();
        }

        [Fact]
        public void Included_fields_are_not_assumed_to_match_stored_index_keys()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("owners").Insert(new BsonDocument { ["_id"] = 1, ["Score"] = 5 });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Owner"] = new BsonDocument { ["$id"] = 1, ["$ref"] = "owners" } });
            rows.EnsureIndex("owner", "Owner.Score");
            var query = rows.Query().Include("Owner").Where("Owner.Score IN [5] OR Owner.Score BETWEEN 8 AND 9");
            query.GetPlan().ContainsKey("filters").Should().BeTrue();
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.ToArray().Single()["Owner"]["Score"].AsInt32.Should().Be(5);
        }

        private static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Score"] = i, ["Other"] = 10 - i, ["Values"] = new BsonArray(1, 9)
            }));
            rows.EnsureIndex("score", "Score");
            rows.EnsureIndex("absolute", "ABS(Score)");
            rows.EnsureIndex("values", "Values[*]");
            return db;
        }
    }
}
