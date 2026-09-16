using System;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private bool _constantFalse;

        private BsonExpression SimplifyPredicate(BsonExpression expression, out bool? constant)
        {
            constant = null;
            if (TryEvaluateBoolean(expression, out var value))
            {
                constant = value;
                return expression;
            }
            var and = expression.Type == BsonExpressionType.And;
            if (!and && expression.Type != BsonExpressionType.Or) return expression;
            var left = SimplifyPredicate(expression.Left, out var leftConstant);
            if (leftConstant.HasValue)
            {
                // Preserve short circuiting: never inspect an unreachable right branch.
                if (leftConstant.Value != and)
                {
                    constant = leftConstant;
                    return left;
                }
                return SimplifyPredicate(expression.Right, out constant);
            }
            var right = SimplifyPredicate(expression.Right, out var rightConstant);
            if (rightConstant == and) return left; // x AND true / x OR false
            // Do not absorb x AND false / x OR true: x may throw or be volatile.
            if (ReferenceEquals(left, expression.Left) && ReferenceEquals(right, expression.Right)) return expression;
            return BsonExpressionFactory.RebuildLogical(expression, left, right);
        }

        private bool TryEvaluateBoolean(BsonExpression expression, out bool value)
        {
            value = false;
            if (!expression.IsScalar || !expression.IsValue || expression.UseSource) return false;
            if (!expression.IsImmutable && expression.Type != BsonExpressionType.Parameter &&
                !(expression.IsPredicate && IsStableValue(expression.Left) && IsStableValue(expression.Right))) return false;
            try
            {
                var result = expression.ExecuteScalar(_collation);
                if (!result.IsBoolean) return false;
                value = result.AsBoolean;
                return true;
            }
            catch (Exception)
            {
                // Leave throwing constants in place so empty inputs and short circuits
                // preserve the execution-time behavior of the original expression.
                return false;
            }
        }
    }
}
