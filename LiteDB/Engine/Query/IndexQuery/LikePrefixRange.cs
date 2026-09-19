using System.Globalization;
using System.Linq;

namespace LiteDB.Engine
{
    /// <summary>
    /// Bounds the index keys that can match a LIKE prefix under a culture collation: [prefix, upper).
    /// LIKE matches char by char, so a matching key is the prefix (up to collation-equal chars) plus any tail. The
    /// index however orders whole strings, where a tail can fuse with the prefix (Danish "aa", Czech "ch", Swedish
    /// "a" + U+0308, Hungarian "ccs") and sort the matching key after unrelated prefixes. Tailorings cannot be
    /// inspected, so the collation is asked instead: a range is only produced for an ASCII letter/digit prefix that
    /// keeps its position in every casing and with every probed follower appended. Everything else full scans.
    /// The probe is one follower deep: a contraction that needs two more chars before it shows is not detected.
    /// </summary>
    internal static class LikePrefixRange
    {
        private const string AsciiAlphanumerics = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        private const char FirstCombiningMark = (char)0x0300;
        private const int CombiningMarkCount = 0x70;

        private const CompareOptions PrimaryStrength =
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth | CompareOptions.IgnoreKanaType;

        // Letters, digits and combining diacritics are what tailorings contract with an ASCII letter.
        private static readonly string _followers = AsciiAlphanumerics +
            new string(Enumerable.Range(FirstCombiningMark, CombiningMarkCount).Select(x => (char)x).ToArray());

        /// <summary>
        /// The probes below were validated against ICU. NLS (.NET Framework, or NLS mode on Windows) keeps some keys
        /// that match the prefix outside the probed range, so it keeps the full scan.
        /// </summary>
        internal static readonly bool IsSupported = UsesIcu();

        // https://learn.microsoft.com/dotnet/core/extensions/globalization-icu#determine-if-your-app-is-using-icu
        private static bool UsesIcu()
        {
            var version = CultureInfo.InvariantCulture.CompareInfo.Version;
            var bytes = version.SortId.ToByteArray();
            var id = bytes[3] << 24 | bytes[2] << 16 | bytes[1] << 8 | bytes[0];

            return id != 0 && id == version.FullVersion;
        }

        /// <summary>
        /// Get the exclusive upper bound (MaxValue when the prefix has no successor) or false when no range is safe.
        /// </summary>
        public static bool TryGetUpperBound(string prefix, Collation collation, out BsonValue upper)
        {
            upper = BsonValue.MaxValue;

            var isPlainCulture = collation.SortOptions == CompareOptions.None || collation.SortOptions == CompareOptions.IgnoreCase;

            if (!IsSupported || !isPlainCulture || prefix.Length == 0 || prefix.Any(c => AsciiAlphanumerics.IndexOf(c) < 0)) return false;

            var bound = GetUpperBound(prefix, collation);
            // a contraction always exists in lower case ("ccs", "Ccs", "CCS" but not "cCs"), whatever the key's casing
            var stem = collation.SortOptions == CompareOptions.IgnoreCase ? prefix.ToLowerInvariant() : prefix;

            upper = bound;

            return IsInside(prefix, prefix, bound, collation) &&
                IsCaseStable(prefix, collation) &&
                _followers.All(follower => IsInside(prefix, stem + follower, bound, collation));
        }

        /// <summary>
        /// A key may spell the prefix in any casing. Contractions are cased as a unit ("aa", "Aa", "AA" but not "aA"),
        /// so a prefix holding one moves when only some of its chars change case - as does "i" under Turkish rules.
        /// </summary>
        private static bool IsCaseStable(string prefix, Collation collation)
        {
            var lowerFirst = new string(prefix.Select((c, i) => i % 2 == 0 ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());
            var upperFirst = new string(prefix.Select((c, i) => i % 2 == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)).ToArray());
            var variants = new[] { prefix.ToLowerInvariant(), prefix.ToUpperInvariant(), lowerFirst, upperFirst };

            return collation.SortOptions == CompareOptions.None || variants.All(variant => collation.Compare(prefix, variant) == 0);
        }

        /// <summary>
        /// Increment the last char that has a successor: "ab" -> "ac", "az" -> "b", "zz" -> unbounded
        /// </summary>
        private static BsonValue GetUpperBound(string prefix, Collation collation)
        {
            for (var i = prefix.Length - 1; i >= 0; i--)
            {
                var successor = GetSuccessor(prefix[i], collation);

                if (successor != null) return prefix.Substring(0, i) + successor;
            }

            return BsonValue.MaxValue;
        }

        /// <summary>
        /// Smallest ASCII letter/digit with a greater primary weight: a case or accent variant is never a bound.
        /// </summary>
        private static string GetSuccessor(char value, Collation collation)
        {
            var compareInfo = collation.Culture.CompareInfo;
            var current = value.ToString();

            return AsciiAlphanumerics
                .Select(c => c.ToString())
                .Where(c => compareInfo.Compare(c, current, PrimaryStrength) > 0)
                .OrderBy(c => c, collation)
                .FirstOrDefault();
        }

        private static bool IsInside(string lower, string value, BsonValue upper, Collation collation)
        {
            return collation.Compare(lower, value) <= 0 &&
                (upper.IsMaxValue || collation.Compare(value, upper.AsString) < 0);
        }
    }
}
