using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class LikeCollation_Tests
    {
        [Fact]
        public void Supplementary_case_pairs_preserve_scalar_character_semantics()
        {
            const string upper = "\U00010400";
            const string lower = "\U00010428";
            var collation = new Collation("/OrdinalIgnoreCase");
            upper.SqlLike(upper + "%", collation).Should().BeTrue();
            lower.SqlLike(upper + "%", collation).Should().BeFalse();
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation });
            var rows = db.GetCollection("rows");
            var values = new[] { upper, lower, upper + "x", lower + "x" };
            rows.Insert(values.Select((value, index) => new BsonDocument { ["_id"] = index + 1, ["Value"] = value }));
            AssertMatches(rows, upper + "%", new[] { 1, 3 });
            rows.EnsureIndex("Value");
            AssertMatches(rows, upper + "%", new[] { 1, 3 });
        }

        [Theory]
        [InlineData("/Ordinal", "A%", new[] { 2, 4 })]
        [InlineData("/Ordinal", "a_", new[] { 3 })]
        [InlineData("/OrdinalIgnoreCase", "A%", new[] { 1, 2, 3, 4 })]
        [InlineData("/OrdinalIgnoreCase", "a_", new[] { 3, 4 })]
        [InlineData("en-US/None", "A%", new[] { 2, 4 })]
        [InlineData("en-US/IgnoreCase", "A%", new[] { 1, 2, 3, 4 })]
        [InlineData("/Ordinal", "1%", new int[0])]
        [InlineData("en-US/IgnoreCase", "1%", new int[0])]
        public void Prefix_results_preserve_collation_and_string_types_across_index_and_reopen(string collation, string pattern, int[] expected)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Collation = new Collation(collation) }))
            {
                var rows = db.GetCollection("rows");
                var values = new BsonValue[] { "a", "A", "ab", "Ab", "B", BsonValue.Null, 12 };
                rows.Insert(values.Select((value, index) => new BsonDocument { ["_id"] = index + 1, ["Value"] = value }));
                AssertMatches(rows, pattern, expected);
                rows.EnsureIndex("Value").Should().BeTrue();
                AssertMatches(rows, pattern, expected);
            }
            using var reopened = new LiteDatabase(file.Filename);
            AssertMatches(reopened.GetCollection("rows"), pattern, expected);
        }

        private static void AssertMatches(ILiteCollection<BsonDocument> rows, string pattern, int[] expected)
        {
            var predicate = BsonExpression.Create("Value LIKE @0", new BsonValue(pattern));
            foreach (var order in new[] { Query.Ascending, Query.Descending })
            {
                rows.Query().Where(predicate).OrderBy("Value", order).ToArray()
                    .Select(row => row["_id"].AsInt32).OrderBy(id => id).Should().Equal(expected);
            }
        }
    }
}
