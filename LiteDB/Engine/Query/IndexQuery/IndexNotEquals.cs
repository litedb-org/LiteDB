using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Scan matching keys in order, seeking past the excluded equal-key range.
    /// </summary>
    internal class IndexNotEquals : Index
    {
        private readonly BsonValue _value;

        public IndexNotEquals(string name, BsonValue value, int order)
            : base(name, order)
        {
            _value = value;
        }

        public override uint GetCost(CollectionIndex index) => 80;

        public override IEnumerable<IndexNode> Execute(IndexService indexer, CollectionIndex index)
        {
            var edge = indexer.GetNode(this.Order == Query.Ascending ? index.Head : index.Tail);
            var next = edge.GetNextPrev(0, this.Order);
            var counter = 0u;
            while (!next.IsEmpty)
            {
                if (counter++ >= indexer.MaxItemsCount)
                {
                    ENSURE(false, "Detected loop in exclusion scan({0})", this.Name);
                }
                var node = indexer.GetNode(next);
                if (node.Key.IsMinValue || node.Key.IsMaxValue) yield break;

                if (node.Key.CompareTo(_value, indexer.Collation) == 0)
                {
                    node = indexer.Find(index, _value, true, this.Order, skipEqual: true);
                    if (node == null) yield break;
                }

                // Callers can release page-backed nodes at a safepoint while
                // suspended. Retain only the next address across each yield.
                next = node.GetNextPrev(0, this.Order);
                yield return node;
                indexer.Safepoint();
            }
        }

        public override string ToString() => string.Format("INDEX SCAN WITH EXCLUSION({0} != {1})", this.Name, _value);
    }
}
