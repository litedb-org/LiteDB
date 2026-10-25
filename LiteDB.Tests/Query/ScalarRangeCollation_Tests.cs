using System;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class ScalarRangeCollation_Tests
    {
        [Theory]
        [InlineData("en-US/IgnoreCase", ">", "a")]
        [InlineData("en-US/IgnoreCase", ">=", "a")]
        [InlineData("en-US/IgnoreCase", "<", "b")]
        [InlineData("en-US/IgnoreCase", "<=", "b")]
        [InlineData("en-US/IgnoreNonSpace", ">", "e")]
        [InlineData("en-US/IgnoreNonSpace", ">=", "e")]
        [InlineData("en-US/IgnoreNonSpace", "<", "e")]
        [InlineData("en-US/IgnoreNonSpace", "<=", "e")]
        [InlineData("en-US/Ordinal", ">", "a")]
        [InlineData("en-US/Ordinal", ">=", "a")]
        [InlineData("en-US/Ordinal", "<", "b")]
        [InlineData("en-US/Ordinal", "<=", "b")]
        public void Scalar_ranges_agree_before_and_after_indexing(string name, string operation, string bound)
        {
            var collation = new Collation(name);
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation });
            var rows = db.GetCollection("rows");
            var values = new[] { "a", "A", "b", "B", "e", "é", "f" };
            rows.InsertBulk(values.Select((value, i) => new BsonDocument { ["_id"] = i + 1, ["Name"] = value }));
            var expected = values.Select((value, i) => new { Id = i + 1, Match = Matches(collation.Compare(value, bound), operation) })
                .Where(x => x.Match).Select(x => x.Id).ToArray();
            var predicate = BsonExpression.Create("Name " + operation + " @0", bound);
            AssertResults();
            rows.EnsureIndex("name", "Name");
            AssertResults();

            // Force a primary-key range so Name remains a residual predicate.
            var residual = rows.Query().Where(BsonExpression.Create("_id > 0 AND Name " + operation + " @0", bound));
            residual.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            residual.ToArray().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(expected);

            void AssertResults()
            {
                rows.Find(predicate).Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(expected);
                rows.Count(predicate).Should().Be(expected.Length);
                Expression<Func<BsonDocument, bool>> linq = operation == ">" ? x => x["Name"] > bound :
                    operation == ">=" ? x => x["Name"] >= bound : operation == "<" ? x => x["Name"] < bound : x => x["Name"] <= bound;
                rows.Find(linq).Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(expected);
            }
        }

        [Theory]
        [InlineData(">")]
        [InlineData(">=")]
        [InlineData("<")]
        [InlineData("<=")]
        public void Reusable_comparisons_read_each_execution_collation(string operation)
        {
            var expression = BsonExpression.Create("Value " + operation + " @0", "a");
            var row = new BsonDocument { ["Value"] = "A" };
            var insensitive = new Collation("en-US/IgnoreCase");
            foreach (var collation in new[] { Collation.Binary, insensitive, Collation.Binary })
            {
                expression.ExecuteScalar(row, collation).AsBoolean.Should().Be(Matches(collation.Compare("A", "a"), operation));
                var rebound = expression.Bind(new BsonDocument { ["0"] = "b" });
                rebound.ExecuteScalar(row, collation).AsBoolean.Should().Be(Matches(collation.Compare("A", "b"), operation));
            }
        }

        private static bool Matches(int comparison, string operation) => operation == ">" ? comparison > 0 :
            operation == ">=" ? comparison >= 0 : operation == "<" ? comparison < 0 : comparison <= 0;
    }
}
