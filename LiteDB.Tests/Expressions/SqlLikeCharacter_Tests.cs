using System;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class SqlLikeCharacter_Tests
    {
        [Theory]
        [InlineData("en-US/None")]
        [InlineData("en-US/IgnoreCase")]
        [InlineData("en-US/IgnoreCase, IgnoreNonSpace")]
        [InlineData("en-US/Ordinal")]
        [InlineData("en-US/OrdinalIgnoreCase")]
        [InlineData("tr-TR/IgnoreCase")]
        [InlineData("ja-JP/IgnoreKanaType, IgnoreWidth")]
        public void Offset_comparisons_match_isolated_strings_for_every_utf16_code_unit(string culture)
        {
            var collation = new Collation(culture);
            for (var value = 0; value <= char.MaxValue; value++)
            {
                var character = ((char)value).ToString();
                var next = ((char)((value + 1) & char.MaxValue)).ToString();
                // Neighbors include a low surrogate and combining mark to ensure
                // the one-code-unit comparison never absorbs adjacent characters.
                var left = "\ud83d" + character + "\ude00\u0301";
                var right = "\ud83d" + next + "\ude00\u0301";
                Assert.Equal(collation.Compare(character, next) == 0, collation.EqualsCharacter(left, 1, right, 1));
                Assert.Equal(collation.Compare(character, "A") == 0, collation.EqualsCharacter(left, 1, "xAy", 1));
                Assert.True(collation.EqualsCharacter(left, 1, left, 1));
            }
        }

        [Theory]
        [InlineData("en-US/None")]
        [InlineData("en-US/IgnoreCase")]
        [InlineData("en-US/IgnoreCase, IgnoreNonSpace")]
        [InlineData("en-US/Ordinal")]
        [InlineData("en-US/OrdinalIgnoreCase")]
        [InlineData("tr-TR/IgnoreCase")]
        [InlineData("ja-JP/IgnoreKanaType, IgnoreWidth")]
        public void Wildcards_match_the_independent_prefix_reachability_specification(string culture)
        {
            var collation = new Collation(culture);
            var strings = new[] { "", "a", "aa", "aaa", "aababa", "ab", "AB", "a\0b", "\0", "\0\0", "abc\0",
                "æae", "äa\u0308", "Iİiı", "カｶか", "\ud83d\ude00x", "\ud83d", "\ude00", "a\u00adb" };
            var patterns = new[] { "", "%", "%%", "_", "__", "a", "aa", "a%", "%a", "%a%", "a%a", "%ab%a", "a_b",
                "a%%b", "%_a%", "\0", "%\0", "a\0%", "a%\0b", "\ud83d%", "%\ude00", "İ%", "%ｶ%", "%\u0308%" };
            foreach (var value in strings)
                foreach (var pattern in patterns)
                    Check(value, pattern);

            var random = new Random(7319);
            const string alphabet = "aAbB%i_İı\0ä\u0308\u00adカｶ\ud83d\ude00";
            for (var i = 0; i < 6000; i++)
            {
                string Word(int length) => new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
                var value = Word(random.Next(24));
                var pattern = Word(random.Next(12));
                Check(value, pattern);
            }
            void Check(string value, string pattern)
            {
                Assert.Equal(SqlLikeReference.Match(value, pattern, collation), value.SqlLike(pattern, collation));
            }
        }
    }
}
