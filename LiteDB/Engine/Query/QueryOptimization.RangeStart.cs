namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private bool PreferRangeStart(IndexCost candidate, IndexCost selected)
        {
            if (candidate.Cost != selected.Cost || candidate.IndexExpression != selected.IndexExpression ||
                !(candidate.Index is IndexRange next) || !(selected.Index is IndexRange previous)) return false;

            var order = Query.Ascending;
            if (_query.OrderBy.Count > 0 && !_query.OrderBy[0].Expression.RequiresExactSort &&
                _query.OrderBy[0].Expression.Source == candidate.IndexExpression)
                order = _query.OrderBy[0].Order;

            // Equal-cost ranges should seek closest to the output's starting end,
            // regardless of predicate order. Other terms remain residual filters:
            // intersecting bounds would be invalid for independent multikey ANYs.
            return next.HasCloserStart(previous, order, _collation);
        }
    }
}
