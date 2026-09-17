using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private IndexCost ChooseRangeDisjunctionIndex(BsonExpression expression, CollectionIndex[] indexes)
        {
            BsonExpression field = null;
            var budget = 64;
            var branches = 0;
            var membership = false;
            if (!ValidateRangeBranches(expression, ref field, ref budget, ref branches, ref membership)) return ChooseNestedBooleanIndex(expression, indexes);
            var index = FindStoredIndex(indexes, field);
            if (index == null) return null;
            if (membership && HasCheaperScalarEquality(indexes)) return null;

            // Validate the entire shape before reading any values. Bindings belong
            // to this invocation; the reusable IR and its parameters stay unchanged.
            try
            {
                var ranges = new List<ScalarBounds>(branches);
                if (!AppendRangeBranches(expression, ranges)) return null;
                return new IndexCost(index, expression, IndexRangeUnion.Create(index.Name, ranges, _collation), scalarKeys: true);
            }
            catch (Exception)
            {
                // Arithmetic can fail for this binding. Let the original filter
                // retain its execution-time errors and short-circuit behavior.
                return null;
            }
        }

        private static bool ValidateRangeBranches(BsonExpression expression, ref BsonExpression field,
            ref int budget, ref int branches, ref bool membership)
        {
            if (expression.Type == BsonExpressionType.Or)
            {
                if (--budget < 0) return false;
                return ValidateRangeBranches(expression.Left, ref field, ref budget, ref branches, ref membership) &&
                    ValidateRangeBranches(expression.Right, ref field, ref budget, ref branches, ref membership);
            }
            branches++;
            return ValidateRangeConjunction(expression, ref field, ref budget, ref membership);
        }

        private static bool ValidateRangeConjunction(BsonExpression expression,
            ref BsonExpression field, ref int budget, ref bool membership)
        {
            if (--budget < 0) return false;
            if (expression.Type == BsonExpressionType.And)
            {
                return ValidateRangeConjunction(expression.Left, ref field, ref budget, ref membership) &&
                    ValidateRangeConjunction(expression.Right, ref field, ref budget, ref membership);
            }
            if (!TryGetUnionConstraint(expression, out var current, out var value, out _) ||
                !IndexExpressionIdentity.IsMemberPath(current) || !IsRangeValue(value, ref budget)) return false;
            if (field != null && !IndexExpressionIdentity.Matches(field.Source, current)) return false;
            field = current;
            membership |= expression.Type == BsonExpressionType.In || expression.IsANY;
            return true;
        }

        private bool AppendRangeBranches(BsonExpression expression, List<ScalarBounds> ranges)
        {
            if (expression.Type == BsonExpressionType.Or)
                return AppendRangeBranches(expression.Left, ranges) && AppendRangeBranches(expression.Right, ranges);
            var constraint = new ScalarIndexConstraint(_collation);
            if (!IntersectRangeConjunction(expression, constraint)) return false;
            constraint.AppendRanges(ranges);
            return true;
        }

        private bool IntersectRangeConjunction(BsonExpression expression, ScalarIndexConstraint constraint)
        {
            if (expression.Type == BsonExpressionType.And)
                return IntersectRangeConjunction(expression.Left, constraint) && IntersectRangeConjunction(expression.Right, constraint);
            TryGetUnionConstraint(expression, out _, out var value, out var operation);
            var current = value.IsScalar ? value.ExecuteScalar(_collation) : new BsonArray(value.Execute(_collation));
            return constraint.Intersect(operation, current);
        }

        private static bool IsRangeValue(BsonExpression value, ref int budget)
        {
            if (--budget < 0) return false;
            if (value.Expression is ConstantExpression || value.Type == BsonExpressionType.Parameter) return true;
            switch (value.Type)
            {
                case BsonExpressionType.Add:
                case BsonExpressionType.Subtract:
                case BsonExpressionType.Multiply:
                case BsonExpressionType.Divide:
                case BsonExpressionType.Modulo:
                    return IsRangeValue(value.Left, ref budget) && IsRangeValue(value.Right, ref budget);
                default: return IsUnionValueExpression(value.Expression, ref budget);
            }
        }

    }
}
