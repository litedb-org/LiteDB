using System.Collections.Generic;

namespace LiteDB.Engine
{
    // Ordered, disjoint intervals permit AND/OR composition without distributing
    // an expression into a potentially exponential number of conjunctions.
    internal static class ScalarIntervals
    {
        internal static bool IsUniversal(List<ScalarBounds> ranges) => ranges.Count == 1 &&
            ranges[0].Lower.IsMinValue && ranges[0].Upper.IsMaxValue && ranges[0].LowerInclusive && ranges[0].UpperInclusive;

        internal static bool Contains(List<ScalarBounds> ranges, BsonValue value, Collation collation)
        {
            var low = 0;
            var high = ranges.Count - 1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                var range = ranges[middle];
                var lower = value.CompareTo(range.Lower, collation);
                if (lower < 0) { high = middle - 1; continue; }
                var upper = value.CompareTo(range.Upper, collation);
                if (upper > 0) { low = middle + 1; continue; }
                return (lower != 0 || range.LowerInclusive) && (upper != 0 || range.UpperInclusive);
            }
            return false;
        }

        internal static List<ScalarBounds> Normalize(List<ScalarBounds> ranges, Collation collation)
        {
            ranges.Sort((a, b) => CompareLower(a, b, collation));
            var result = new List<ScalarBounds>();
            foreach (var range in ranges) Append(result, range, collation);
            return result;
        }

        internal static List<ScalarBounds> Union(List<ScalarBounds> left, List<ScalarBounds> right, Collation collation)
        {
            var result = new List<ScalarBounds>();
            var i = 0;
            var j = 0;
            while (i < left.Count || j < right.Count)
            {
                var next = j == right.Count || (i < left.Count && CompareLower(left[i], right[j], collation) <= 0) ? left[i++] : right[j++];
                Append(result, next, collation);
            }
            return result;
        }

        internal static List<ScalarBounds> Intersect(List<ScalarBounds> left, List<ScalarBounds> right, Collation collation)
        {
            var result = new List<ScalarBounds>();
            var i = 0;
            var j = 0;
            while (i < left.Count && j < right.Count)
            {
                var a = left[i];
                var b = right[j];
                var low = a.Lower.CompareTo(b.Lower, collation);
                var high = a.Upper.CompareTo(b.Upper, collation);
                var lower = low > 0 ? a.Lower : b.Lower;
                var upper = high < 0 ? a.Upper : b.Upper;
                var lowerInclusive = low == 0 ? a.LowerInclusive && b.LowerInclusive : low > 0 ? a.LowerInclusive : b.LowerInclusive;
                var upperInclusive = high == 0 ? a.UpperInclusive && b.UpperInclusive : high < 0 ? a.UpperInclusive : b.UpperInclusive;
                var overlap = lower.CompareTo(upper, collation);
                // Large point sets often have few surviving keys. Allocate only
                // the intersections that contribute to the resulting scan.
                if (overlap < 0 || (overlap == 0 && lowerInclusive && upperInclusive))
                    Append(result, new ScalarBounds(lower, upper, lowerInclusive, upperInclusive), collation);
                if (high <= 0) i++;
                if (high >= 0) j++;
            }
            return result;
        }

        private static int CompareLower(ScalarBounds a, ScalarBounds b, Collation collation)
        {
            var lower = a.Lower.CompareTo(b.Lower, collation);
            // An inclusive point can bridge ranges with open endpoints.
            return lower != 0 ? lower : b.LowerInclusive.CompareTo(a.LowerInclusive);
        }

        private static void Append(List<ScalarBounds> ranges, ScalarBounds next, Collation collation)
        {
            if (ranges.Count == 0) { ranges.Add(next); return; }
            var previous = ranges[ranges.Count - 1];
            var gap = next.Lower.CompareTo(previous.Upper, collation);
            if (gap > 0 || (gap == 0 && !next.LowerInclusive && !previous.UpperInclusive))
            {
                ranges.Add(next);
                return;
            }
            var upper = next.Upper.CompareTo(previous.Upper, collation);
            ranges[ranges.Count - 1] = new ScalarBounds(previous.Lower, upper > 0 ? next.Upper : previous.Upper,
                previous.LowerInclusive || (next.LowerInclusive && next.Lower.CompareTo(previous.Lower, collation) == 0),
                upper > 0 ? next.UpperInclusive : previous.UpperInclusive || (upper == 0 && next.UpperInclusive));
        }
    }
}
