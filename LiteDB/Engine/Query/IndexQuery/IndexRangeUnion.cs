using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal sealed class IndexRangeUnion : Index
    {
        private readonly List<ScalarBounds> _ranges;

        private IndexRangeUnion(string name, List<ScalarBounds> ranges) : base(name, Query.Ascending)
        {
            _ranges = ranges;
        }

        internal static Index Create(string name, List<ScalarBounds> ranges, Collation collation)
        {
            if (ranges.Count == 0) return new IndexEmpty();
            ranges.Sort((a, b) =>
            {
                var lower = a.Lower.CompareTo(b.Lower, collation);
                // Process a covered boundary first, so a point bridging two open
                // ranges also collapses them into one scan regardless of OR order.
                return lower != 0 ? lower : b.LowerInclusive.CompareTo(a.LowerInclusive);
            });
            var merged = new List<ScalarBounds>();
            var previous = ranges[0];
            for (var i = 1; i < ranges.Count; i++)
            {
                var next = ranges[i];
                var gap = next.Lower.CompareTo(previous.Upper, collation);
                if (gap > 0 || (gap == 0 && !next.LowerInclusive && !previous.UpperInclusive))
                {
                    merged.Add(previous);
                    previous = next;
                    continue;
                }
                var upper = next.Upper.CompareTo(previous.Upper, collation);
                previous = new ScalarBounds(previous.Lower, upper > 0 ? next.Upper : previous.Upper,
                    previous.LowerInclusive || (next.LowerInclusive && next.Lower.CompareTo(previous.Lower, collation) == 0),
                    upper > 0 ? next.UpperInclusive : previous.UpperInclusive || (upper == 0 && next.UpperInclusive));
            }
            merged.Add(previous);
            return merged.Count == 1 ? (Index)Scan(name, merged[0], Query.Ascending) : new IndexRangeUnion(name, merged);
        }

        public override uint GetCost(CollectionIndex index) => (uint)_ranges.Count * 20;

        public override IEnumerable<IndexNode> Execute(IndexService indexer, CollectionIndex index)
        {
            // Merged intervals are disjoint and the planner proved one scalar key
            // per document. No address set is needed, and ORDER BY can use this order.
            for (var i = 0; i < _ranges.Count; i++)
            {
                var range = _ranges[Order == Query.Ascending ? i : _ranges.Count - i - 1];
                foreach (var node in Scan(Name, range, Order).Execute(indexer, index)) yield return node;
                indexer.Safepoint();
            }
        }

        private static IndexRange Scan(string name, ScalarBounds range, int order) =>
            new IndexRange(name, range.Lower, range.Upper, range.LowerInclusive, range.UpperInclusive, order);

        public override string ToString() => "INDEX RANGE UNION(" + Name + ": " + string.Join(", ", _ranges.Select(range =>
            (range.LowerInclusive ? "[" : "(") + JsonSerializer.Serialize(range.Lower) + ", " +
            JsonSerializer.Serialize(range.Upper) + (range.UpperInclusive ? "]" : ")"))) + ")";
    }
}
