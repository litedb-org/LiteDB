namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private static bool TryGetScalarBound(BsonExpression expression, out BsonExpression field,
            out BsonExpression value, out BsonExpressionType operation, bool allowComputedValue = false)
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
            return field.IsScalar && field.IsImmutable && !field.IsVolatile && !field.IsValue &&
                (IsStableValue(value) || (allowComputedValue && value.IsScalar && value.IsValue && !value.UseSource && !value.IsVolatile));
        }
    }
}
