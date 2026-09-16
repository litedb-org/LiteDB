using System.Collections.Generic;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private void NarrowRange(BsonExpression selected)
        {
            if (!(_queryPlan.Index is IndexRange range) ||
                !TryGetScalarBound(selected, out var field, out _, out _)) return;
            var bounds = new ScalarBounds(range.Start, range.End, range.StartEquals, range.EndEquals);
            var consumed = new List<BsonExpression>();
            foreach (var filter in _queryPlan.Filters)
            {
                if (!TryGetScalarBound(filter, out var other, out var value, out var operation) ||
                    field.Source != other.Source) continue;
                bounds.Intersect(operation, value.ExecuteScalar(_collation), _collation);
                consumed.Add(filter);
            }
            // Empty intersections are handled separately; IndexRange expects a valid interval.
            if (consumed.Count == 0 || bounds.IsEmpty(_collation)) return;
            _queryPlan.Index = new IndexRange(range.Name, bounds.Lower, bounds.Upper,
                bounds.LowerInclusive, bounds.UpperInclusive, range.Order);
            foreach (var filter in consumed) _queryPlan.Filters.Remove(filter);
        }

        private static bool TryGetScalarBound(BsonExpression expression, out BsonExpression field,
            out BsonExpression value, out BsonExpressionType operation)
        {
            field = expression?.Left;
            value = expression?.Right;
            operation = expression?.Type ?? default;
            if (field == null || value == null) return false;
            switch (operation)
            {
                case BsonExpressionType.Equal:
                case BsonExpressionType.GreaterThan:
                case BsonExpressionType.GreaterThanOrEqual:
                case BsonExpressionType.LessThan:
                case BsonExpressionType.LessThanOrEqual: break;
                default: return false;
            }
            if (field.IsValue)
            {
                field = expression.Right;
                value = expression.Left;
                switch (operation)
                {
                    case BsonExpressionType.GreaterThan: operation = BsonExpressionType.LessThan; break;
                    case BsonExpressionType.GreaterThanOrEqual: operation = BsonExpressionType.LessThanOrEqual; break;
                    case BsonExpressionType.LessThan: operation = BsonExpressionType.GreaterThan; break;
                    case BsonExpressionType.LessThanOrEqual: operation = BsonExpressionType.GreaterThanOrEqual; break;
                }
            }
            // Two ANY predicates may be satisfied by different array elements.
            return field.IsScalar && field.IsImmutable && !field.IsValue && IsStableValue(value);
        }
    }
}
