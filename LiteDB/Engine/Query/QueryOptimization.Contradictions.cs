using System;
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
                    !IndexExpressionIdentity.IsMemberPath(field)) return;
                // Only a safe prefix can disappear. An earlier residual predicate
                // may throw or be volatile even if later bounds cannot both match.
                if (fields == null) fields = new Dictionary<string, ScalarBounds>();
                if (!fields.TryGetValue(field.Source, out var bounds))
                {
                    bounds = new ScalarBounds(BsonValue.MinValue, BsonValue.MaxValue, true, true);
                    fields.Add(field.Source, bounds);
                }
                try
                {
                    bounds.Intersect(operation, value.ExecuteScalar(_collation), _collation);
                    if (!bounds.IsEmpty(_collation)) continue;
                }
                catch (Exception)
                {
                    // Speculative evaluation must not introduce errors on empty
                    // input or ahead of a filter's ordinary short circuit.
                    return;
                }
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
