using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Issues
{
    public class Issue2144_PrefixSeek_Tests
    {
        // 900 random strings over 13 collations (adds sv-SE, cs-CZ, hr-HR, de-DE, hu-HU/IgnoreCase, /IgnoreCase, /Ordinal)
        // ran clean locally with every prefix; sampling keeps the theory to a few seconds per CI job.
        private const int RandomStringCount = 150;
        private const int PlainQuerySampleStep = 5;
        private const int PrefixSampleStep = 5;

        // Every atom is a known way to break "matching keys are contiguous after the prefix" under some collation.
        private static readonly string[] _atoms =
        {
            "a", "A", "b", "B", "c", "C", "d", "h", "H", "k", "s", "S", "v", "w", "z", "Z", "0", "1", "9",
            "i", "I", "\u0130", "\u0131", "\u00DF", "ss", "SS", "\u00C6", "ae", "AE", "aa", "ch", "cs", "dz",
            "ccs", "ddzs", "sz", "ll", "nj", "ng",
            "\u00AD", "\u200D", "e\u0301", "\u00E9", "\u0301", "\u0308", "\u0327", "\u00E7", "\u00E4", "\u00E5",
            "\u017F", "\u212A", "\U00010400", "\U00010428", " ", "-"
        };

        private static readonly BsonValue[] _nonStrings =
        {
            BsonValue.Null, 12, 1.5, true, new BsonDocument { ["a"] = "a" }, new DateTime(2020, 1, 1), new byte[] { 97 }
        };

        private readonly ITestOutputHelper _output;

        public Issue2144_PrefixSeek_Tests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData("en-US/IgnoreCase")]
        [InlineData("en-US/None")]
        [InlineData("tr-TR/IgnoreCase")]
        [InlineData("da-DK/IgnoreCase")]
        [InlineData("hu-HU/None")]
        [InlineData("/OrdinalIgnoreCase")]
        public void Indexed_prefix_results_equal_the_unindexed_filter_for_adversarial_strings(string collationName)
        {
            var collation = new Collation(collationName);
            var values = CreateValues();
            var docs = values.Select((value, index) => new BsonDocument { ["_id"] = index + 1, ["Value"] = value }).ToArray();

            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation });
            var plain = db.GetCollection("plain");
            var indexed = db.GetCollection("indexed");
            plain.Insert(docs);
            indexed.Insert(docs);
            indexed.EnsureIndex("Value").Should().BeTrue();

            var mismatches = new List<string>();
            var prefixes = CreatePrefixes(values);
            // the ordinal seek predates the culture range seek; its descending walk is a separate, older defect
            var isOrdinal = collation.SortOptions == CompareOptions.Ordinal || collation.SortOptions == CompareOptions.OrdinalIgnoreCase;
            var orders = isOrdinal ? new[] { Query.Ascending } : new[] { Query.Ascending, Query.Descending };

            for (var i = 0; i < prefixes.Length; i++)
            {
                var pattern = prefixes[i] + "%";
                var expected = Enumerable.Range(0, values.Length)
                    .Where(x => values[x].IsString && values[x].AsString.SqlLike(pattern, collation)).Select(x => x + 1).ToArray();

                // the matcher is only a fast oracle: keep it honest against the real unindexed query
                if (i % PlainQuerySampleStep == 0)
                {
                    QueryIds(plain, prefixes[i], Query.Ascending, collation).OrderBy(id => id).Should().Equal(expected);
                }

                foreach (var order in orders)
                {
                    var actual = QueryIds(indexed, prefixes[i], order, collation).OrderBy(id => id).ToArray();

                    if (!actual.SequenceEqual(expected))
                    {
                        mismatches.Add($"'{Escape(prefixes[i])}' order {order}: expected {expected.Length} rows, got {actual.Length}");
                    }
                }
            }

            var seeks = prefixes.Count(prefix => GetIndexMode(indexed, prefix + "%").StartsWith("INDEX SEEK"));

            _output.WriteLine($"{collationName}: {values.Length} values, {prefixes.Length} prefixes, {seeks} seeks, {mismatches.Count} mismatches");
            var canSeek = collation.SortOptions == CompareOptions.OrdinalIgnoreCase || LiteDB.Engine.LikePrefixRange.IsSupported;
            (seeks > 0).Should().Be(canSeek, "the culture range seek is ICU-only; a differential run that never seeks proves nothing about it");
            mismatches.Should().BeEmpty();
        }

        [Theory]
        [InlineData("en-US/IgnoreCase", "ab%")]
        [InlineData("en-US/None", "Ab%")]
        [InlineData("de-DE/IgnoreCase", "z9%")]
        [InlineData("tr-TR/IgnoreCase", "ab_d%")]
        public void Ascii_prefix_under_a_culture_collation_uses_an_index_seek(string collationName, string pattern)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation(collationName) });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["Value"] = "abcd" });
            rows.EnsureIndex("Value");

            // NLS (.NET Framework) keeps matching keys outside the probed range, so it keeps the full scan
            GetIndexMode(rows, pattern).Should().StartWith(LiteDB.Engine.LikePrefixRange.IsSupported ? "INDEX SEEK" : "FULL INDEX SCAN");
        }

        [Theory]
        [InlineData("da-DK/IgnoreCase", "a%")]
        [InlineData("cs-CZ/IgnoreCase", "c%")]
        [InlineData("hu-HU/IgnoreCase", "cc%")]
        [InlineData("en-US/IgnoreCase", "\u00E9%")]
        [InlineData("en-US/IgnoreNonSpace", "a%")]
        public void Prefix_whose_matches_can_sort_outside_its_range_keeps_the_full_index_scan(string collationName, string pattern)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation(collationName) });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["Value"] = "abcd" });
            rows.EnsureIndex("Value");

            GetIndexMode(rows, pattern).Should().StartWith("FULL INDEX SCAN");
        }

        private static string GetIndexMode(ILiteCollection<BsonDocument> rows, string pattern)
        {
            var plan = rows.Query().Where(BsonExpression.Create("Value LIKE @0", new BsonValue(pattern))).GetPlan();

            return plan["index"]["mode"].AsString;
        }

        private static int[] QueryIds(ILiteCollection<BsonDocument> rows, string prefix, int order, Collation collation)
        {
            var predicate = BsonExpression.Create("Value LIKE @0", new BsonValue(prefix + "%"));
            var result = rows.Query().Where(predicate).OrderBy("Value", order).ToArray();

            for (var i = 1; i < result.Length; i++)
            {
                (collation.Compare(result[i - 1]["Value"], result[i]["Value"]) * order).Should().BeLessThanOrEqualTo(0);
            }

            return result.Select(row => row["_id"].AsInt32).ToArray();
        }

        private static BsonValue[] CreateValues()
        {
            var random = new Random(2144);
            var pairs = _atoms.SelectMany(first => _atoms.Select(second => first + second));
            var longer = Enumerable.Range(0, RandomStringCount)
                .Select(_ => string.Concat(Enumerable.Range(0, random.Next(3, 6)).Select(__ => _atoms[random.Next(_atoms.Length)])));

            return new[] { "" }.Concat(_atoms).Concat(pairs).Concat(longer)
                .Select(value => new BsonValue(value)).Concat(_nonStrings).ToArray();
        }

        private static string[] CreatePrefixes(IEnumerable<BsonValue> values)
        {
            return values.Where(value => value.IsString).Select(value => value.AsString)
                .SelectMany(value => Enumerable.Range(1, 4).Where(length => length <= value.Length).Select(length => value.Substring(0, length)))
                .Distinct(StringComparer.Ordinal).OrderBy(prefix => prefix, StringComparer.Ordinal)
                .Where((prefix, index) => index % PrefixSampleStep == 0).ToArray();
        }

        private static string Escape(string value)
        {
            return string.Concat(value.Select(c => c < 0x80 && c >= 0x20 ? c.ToString() : $"\\u{(int)c:X4}"));
        }
    }
}
