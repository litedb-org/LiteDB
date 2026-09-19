using System;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private bool _constantFalse;

        private BsonExpression SimplifyPredicate(BsonExpression expression, out bool? constant)
        {
            constant = null;
            if (TryUnwrapBooleanPredicate(expression, out var predicate))
                return SimplifyPredicate(predicate, out constant);
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

        private bool TryUnwrapBooleanPredicate(BsonExpression expression, out BsonExpression predicate)
        {
            predicate = null;
            if (expression.Type != BsonExpressionType.Equal && expression.Type != BsonExpressionType.NotEqual) return false;
            if (expression.IsVolatile) return false;
            var left = expression.Left;
            var right = expression.Right;
            if (IsBooleanPredicate(left) && TryEvaluateBoolean(right, out var rhs) &&
                rhs == (expression.Type == BsonExpressionType.Equal)) predicate = left;
            else if (IsBooleanPredicate(right) && TryEvaluateBoolean(left, out var lhs) &&
                lhs == (expression.Type == BsonExpressionType.Equal)) predicate = right;
            return predicate != null;
        }

        // Only predicates and logical operators guarantee Boolean results. A path
        // can contain null, numbers, or strings, so its comparison must remain.
        private static bool IsBooleanPredicate(BsonExpression expression) => expression != null &&
            (expression.IsPredicate || expression.Type == BsonExpressionType.And || expression.Type == BsonExpressionType.Or);

        private bool TryEvaluateBoolean(BsonExpression expression, out bool value)
        {
            value = false;
            if (!expression.IsScalar || !expression.IsValue || expression.UseSource || expression.IsVolatile) return false;
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
