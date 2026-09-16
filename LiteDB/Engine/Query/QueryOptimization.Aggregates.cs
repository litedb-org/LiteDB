namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private void DefineRowAggregate()
        {
            if (!_queryPlan.Select.All || _queryPlan.GroupBy != null || _queryPlan.Filters.Count != 0 ||
                _queryPlan.OrderBy != null || _queryPlan.IncludeBefore.Count != 0 || _queryPlan.IncludeAfter.Count != 0 ||
                _queryPlan.ForUpdate || _queryPlan.VectorScore != null ||
                _queryPlan.Index is IndexVirtual || _queryPlan.Index is VectorIndexQuery) return;
            _queryPlan.RowAggregate = RowAggregate.TryCreate(_queryPlan.Select.Expression);
        }
    }
}
