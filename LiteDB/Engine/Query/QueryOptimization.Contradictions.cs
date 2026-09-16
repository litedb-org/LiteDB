using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private void PruneContradictions(IReadOnlyCollection<BsonExpression> consumed)
        {
            if (_terms.Count - (consumed?.Count ?? 0) < 2) return;
            Dictionary<string, ScalarBounds> fields = null;
            foreach (var term in _terms)
            {
                if (consumed?.Contains(term) == true) continue;
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
                this.UseEmptyInput();
                return;
            }
        }

        private void UseEmptyInput()
        {
            _queryPlan.Index = new IndexEmpty();
            _queryPlan.IndexExpression = "$._id";
            _queryPlan.IndexCost = 0;
            _queryPlan.IsIndexKeyOnly = false;
            _queryPlan.Filters.Clear();
            _vectorOrderConsumed = false;
            _vectorPrimaryOrderMatched = false;
        }
    }
}
