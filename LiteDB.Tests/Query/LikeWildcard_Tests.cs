using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class LikeWildcard_Tests
    {
        [Fact]
        public void Small_patterns_match_an_independent_anchored_regex()
        {
            var values = Words("ab", 5).ToArray();
            foreach (var pattern in Words("ab%_", 4))
            {
                var oracle = new Regex("\\A" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "\\z",
                    RegexOptions.CultureInvariant | RegexOptions.Singleline);
                foreach (var value in values)
                {
                    value.SqlLike(pattern, Collation.Binary).Should().Be(oracle.IsMatch(value),
                        "value {0} and pattern {1} must match completely", value, pattern);
                }
            }
        }

        [Theory]
        [InlineData("ab_")]
        [InlineData("a%%b")]
        [InlineData("a%_b")]
        [InlineData("%_%")]
        public void Indexed_wildcards_match_scan_and_independent_length_boundaries(string pattern)
        {
            var values = new[] { "", "a", "ab", "abb", "abbb", "acb", "acxb", "ba", "b" };
            var oracle = new Regex("\\A" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "\\z");
            var expected = values.Select((value, index) => new { value, id = index + 1 })
                .Where(row => oracle.IsMatch(row.value)).Select(row => row.id).ToArray();
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = Collation.Binary });
            var rows = db.GetCollection("rows");
            rows.Insert(values.Select((value, index) => new BsonDocument { ["_id"] = index + 1, ["Value"] = value }));
            foreach (var indexed in new[] { false, true })
            {
                if (indexed) rows.EnsureIndex("Value");
                rows.Find(BsonExpression.Create("Value LIKE @0", new BsonValue(pattern)))
                    .Select(row => row["_id"].AsInt32).OrderBy(id => id)
                    .Should().Equal(expected);
            }
        }

        private static IEnumerable<string> Words(string alphabet, int maximumLength)
        {
            yield return "";
            if (maximumLength == 0) yield break;
            foreach (var prefix in Words(alphabet, maximumLength - 1))
            {
                foreach (var character in alphabet) yield return prefix + character;
            }
        }
    }
}
