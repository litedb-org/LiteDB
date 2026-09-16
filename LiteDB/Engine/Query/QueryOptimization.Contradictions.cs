using System.Collections.Generic;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private void PruneContradictions()
        {
            if (_terms.Count < 2) return;
            Dictionary<string, ScalarBounds> fields = null;
            foreach (var term in _terms)
            {
                if (!TryGetScalarBound(term, out var field, out var value, out var operation) ||
                    field.Type != BsonExpressionType.Path) continue;
                // Restrict proof to paths: a deterministic function can still throw.
                // This also excludes volatile functions and ANY/ALL element predicates.
                if (fields == null) fields = new Dictionary<string, ScalarBounds>();
                if (!fields.TryGetValue(field.Source, out var bounds))
                {
                    bounds = new ScalarBounds(BsonValue.MinValue, BsonValue.MaxValue, true, true);
                    fields.Add(field.Source, bounds);
                }
                bounds.Intersect(operation, value.ExecuteScalar(_collation), _collation);
                if (!bounds.IsEmpty(_collation)) continue;
                _queryPlan.Index = new IndexEmpty();
                _queryPlan.IndexExpression = "$._id";
                _queryPlan.IndexCost = 0;
                _queryPlan.IsIndexKeyOnly = false;
                _queryPlan.Filters.Clear();
                return;
            }
        }
    }
}
