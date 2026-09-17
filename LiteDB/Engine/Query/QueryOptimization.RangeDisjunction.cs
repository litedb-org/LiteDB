using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private IndexCost ChooseRangeDisjunctionIndex(BsonExpression expression, CollectionIndex[] indexes)
        {
            // Included documents can change the field values before filtering.
            if (_query.Includes.Count != 0) return null;
            var branches = new List<List<BsonExpression>>();
            BsonExpression field = null;
            var budget = 64;
            if (!CollectRangeBranches(expression, branches, ref field, ref budget)) return null;
            var index = indexes.FirstOrDefault(x => IndexExpressionIdentity.Matches(x.Expression, field));
            if (index == null) return null;
            if (_terms.Count > 1 && branches.Any(branch => branch.Any(term => term.Type == BsonExpressionType.In || term.IsANY)) &&
                HasCheaperScalarEquality(indexes)) return null;

            // Validate the entire shape before reading any values. Bindings belong
            // to this invocation; the reusable IR and its parameters stay unchanged.
            try
            {
                var ranges = new List<ScalarBounds>(branches.Count);
                foreach (var branch in branches)
                {
                    var constraint = new ScalarIndexConstraint(_collation);
                    foreach (var term in branch)
                    {
                        TryGetUnionConstraint(term, out _, out var value, out var operation);
                        var current = value.IsScalar ? value.ExecuteScalar(_collation) : new BsonArray(value.Execute(_collation));
                        if (!constraint.Intersect(operation, current)) return null;
                    }
                    constraint.AppendRanges(ranges);
                }
                return new IndexCost(index, expression, IndexRangeUnion.Create(index.Name, ranges, _collation), scalarKeys: true);
            }
            catch (Exception)
            {
                // Arithmetic can fail for this binding. Let the original filter
                // retain its execution-time errors and short-circuit behavior.
                return null;
            }
        }

        private static bool CollectRangeBranches(BsonExpression expression, List<List<BsonExpression>> branches,
            ref BsonExpression field, ref int budget)
        {
            if (expression.Type == BsonExpressionType.Or)
            {
                if (--budget < 0) return false;
                return CollectRangeBranches(expression.Left, branches, ref field, ref budget) &&
                    CollectRangeBranches(expression.Right, branches, ref field, ref budget);
            }
            var branch = new List<BsonExpression>();
            branches.Add(branch);
            return CollectRangeConjunction(expression, branch, ref field, ref budget);
        }

        private static bool CollectRangeConjunction(BsonExpression expression, List<BsonExpression> branch,
            ref BsonExpression field, ref int budget)
        {
            if (--budget < 0) return false;
            if (expression.Type == BsonExpressionType.And)
            {
                return CollectRangeConjunction(expression.Left, branch, ref field, ref budget) &&
                    CollectRangeConjunction(expression.Right, branch, ref field, ref budget);
            }
            if (!TryGetUnionConstraint(expression, out var current, out var value, out _) ||
                !IsRangeMemberPath(current) || !IsRangeValue(value, ref budget)) return false;
            if (field != null && !IndexExpressionIdentity.Matches(field.Source, current)) return false;
            field = current;
            branch.Add(expression);
            return true;
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

        private static bool IsRangeMemberPath(BsonExpression field)
        {
            // Member reads are total even for missing/non-document values. Computed
            // keys, array selectors and bound calls may throw in a skipped OR arm.
            if (field.Type != BsonExpressionType.Path) return false;
            var expression = field.Expression;
            var members = 0;
            while (expression is MethodCallExpression member && member.Method == BsonExpressionFactory._memberPathMethod &&
                member.Arguments[1] is ConstantExpression)
            {
                if (++members > 64) return false;
                expression = member.Arguments[0];
            }
            return members != 0 && expression is ParameterExpression parameter &&
                (parameter.Type == typeof(BsonDocument) || parameter.Type == typeof(BsonValue));
        }
    }
}
