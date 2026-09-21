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
            return FromNormalized(name, ScalarIntervals.Normalize(ranges, collation));
        }

        internal static Index FromNormalized(string name, List<ScalarBounds> ranges)
        {
            if (ranges.Count == 0) return new IndexEmpty();
            return ranges.Count == 1 ? (Index)Scan(name, ranges[0], Query.Ascending) : new IndexRangeUnion(name, ranges);
        }

        public override uint GetCost(CollectionIndex index) => (uint)_ranges.Count * 20;

        public override IEnumerable<IndexNode> Execute(IndexService indexer, CollectionIndex index)
        {
            // Merged intervals are disjoint and the planner proved one scalar key
            // per document. Read queries need no address set; ORDER BY can use this order.
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
