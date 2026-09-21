namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private void DefineBorrowedExecution()
        {
            if (_queryPlan.IncludeBefore.Count > 0) return;

            if (_queryPlan.Filters.Count > 0 &&
                BorrowedPredicateEvaluator.TryCreate(_queryPlan.Filters, out var predicate))
            {
                _queryPlan.BorrowedFilter = predicate;
            }

            if (_queryPlan.OrderBy != null &&
                (_queryPlan.Filters.Count == 0 || _queryPlan.BorrowedFilter != null) &&
                BorrowedScalarEvaluator.TryCreate(_queryPlan.OrderBy.Segments, out var scalar))
            {
                _queryPlan.BorrowedOrderBy = scalar;
            }

            if (_queryPlan.OrderBy == null && _queryPlan.IncludeAfter.Count == 0 &&
                _queryPlan.VectorScore == null && !_queryPlan.Select.All &&
                (_queryPlan.Filters.Count == 0 || _queryPlan.BorrowedFilter != null) &&
                BorrowedProjectionEvaluator.TryCreate(_queryPlan.Select.Expression, out var projection))
            {
                _queryPlan.BorrowedProjection = projection;
            }
        }
    }
}
