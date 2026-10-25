using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class SqlLikeWildcard_Tests
    {
        [Theory]
        [InlineData("", "", true)]
        [InlineData("", "%%", true)]
        [InlineData("", "_", false)]
        [InlineData("a", "%_", true)]
        [InlineData("ab", "%_b", true)]
        [InlineData("ab", "%__", true)]
        [InlineData("ab", "%___", false)]
        [InlineData("aab", "a%b", true)]
        [InlineData("ababa", "%aba", true)]
        [InlineData("ababa", "a%ba", true)]
        [InlineData("ababa", "a%b%c", false)]
        [InlineData("zzaXbra", "%a_bra", true)]
        [InlineData("aa", "a", false)]
        [InlineData("baaa", "%ba", false)]
        [InlineData("Person12344", "%234", false)]
        [InlineData("x\0y", "x_y", true)]
        [InlineData("x\0y", "x%y", true)]
        [InlineData("x\0", "x", false)]
        [InlineData("\ud83d\ude00", "_", false)]
        [InlineData("\ud83d\ude00", "__", true)]
        [InlineData("[a]", "[a]", true)]
        [InlineData("a", "[a]", false)]
        public void Matching_consumes_the_whole_value_with_percent_and_single_unit_wildcards(string value, string pattern, bool expected)
        {
            value.SqlLike(pattern, Collation.Binary).Should().Be(expected);
        }

        [Fact]
        public void Sql_and_linq_suffixes_exclude_extra_trailing_characters_before_and_after_indexing()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<QueryTest.SqlLikeExecution_Tests.Row>("rows");
            rows.InsertBulk(new[] { "Person1234", "Person12344", "Person234", "Person2344", "Other" }
                .Select((name, i) => new QueryTest.SqlLikeExecution_Tests.Row { Id = i + 1, Name = name }));
            for (var indexed = 0; indexed < 2; indexed++)
            {
                rows.Find(x => x.Name.EndsWith("234")).Select(x => x.Id).Should().BeEquivalentTo(new[] { 1, 3 });
                foreach (var pattern in new[] { "%234", "%_234", "%234" })
                {
                    using var reader = db.Execute("SELECT _id FROM rows WHERE Name LIKE @pattern", new BsonDocument { ["pattern"] = pattern });
                    reader.ToArray().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 3 });
                }
                rows.EnsureIndex(x => x.Name);
            }
        }
    }
}
