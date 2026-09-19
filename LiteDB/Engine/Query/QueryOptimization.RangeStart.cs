using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private readonly HashSet<BsonExpression> _fusedTerms = new HashSet<BsonExpression>();

        private IndexCost SelectLowest(IndexCost lowest, IndexCost current, List<IndexCost> ranges)
        {
            // Each ANY term may be satisfied by a different array item, so only scalar ranges can be intersected.
            if (current.Index is IndexRange && !current.Expression.IsANY) ranges.Add(current);

            return lowest == null || current.Cost < lowest.Cost || this.PreferRangeStart(current, lowest) ? current : lowest;
        }

        /// <summary>
        /// Replace the selected range by the intersection of every scalar range over the same index, so
        /// `x >= low AND x <= high` scans [low, high] instead of running from one bound to the end of the index.
        /// </summary>
        private IndexCost FuseRanges(IndexCost lowest, List<IndexCost> ranges)
        {
            var fused = ranges.Where(x => x.IndexExpression == lowest.IndexExpression).ToList();

            if (fused.Count < 2 || !fused.Contains(lowest)) return lowest;

            _fusedTerms.UnionWith(fused.Select(x => x.Expression));

            var range = fused.Select(x => (IndexRange)x.Index).Aggregate((left, right) => left.Intersect(right, _collation));

            return new IndexCost(lowest, range);
        }

        private bool PreferRangeStart(IndexCost candidate, IndexCost selected)
        {
            if (candidate.Cost != selected.Cost || candidate.IndexExpression != selected.IndexExpression ||
                !(candidate.Index is IndexRange next) || !(selected.Index is IndexRange previous)) return false;

            var order = Query.Ascending;
            if (_query.OrderBy.Count > 0 && !_query.OrderBy[0].Expression.RequiresExactSort &&
                _query.OrderBy[0].Expression.Source == candidate.IndexExpression)
                order = _query.OrderBy[0].Order;

            // Equal-cost ranges that cannot be fused (independent multikey ANYs) should seek
            // closest to the output's starting end, regardless of predicate order.
            return next.HasCloserStart(previous, order, _collation);
        }
    }
}
