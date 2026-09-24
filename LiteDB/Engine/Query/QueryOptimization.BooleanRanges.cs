using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private HashSet<BsonExpression> _booleanCoveredTerms;

        private IndexCost ChooseNestedBooleanIndex(BsonExpression expression, CollectionIndex[] indexes)
        {
            if (HasCheaperScalarEquality(indexes)) return null;
            BsonExpression field = null;
            var budget = 64;
            if (!ValidateBooleanRanges(expression, ref field, ref budget)) return null;
            var index = FindStoredIndex(indexes, field);
            if (index == null) return null;
            return TryBooleanRangeIndex(index, new List<BsonExpression> { expression });
        }

        private IndexCost ChooseBooleanIntersectionIndex(CollectionIndex[] indexes)
        {
            if (_terms.Count < 2 || _terms.Count > 64 ||
                !_terms.Any(x => x.Type == BsonExpressionType.Or) || HasCheaperScalarEquality(indexes)) return null;
            var groups = new Dictionary<CollectionIndex, List<BsonExpression>>();
            var budget = 64;
            foreach (var term in _terms)
            {
                BsonExpression field = null;
                var valid = ValidateBooleanRanges(BooleanSource(term), ref field, ref budget);
                if (budget < 0) return null;
                if (!valid) continue;
                var index = FindStoredIndex(indexes, field);
                if (index == null) continue;
                if (!groups.TryGetValue(index, out var expressions)) groups.Add(index, expressions = new List<BsonExpression>());
                expressions.Add(term);
            }
            IndexCost best = null;
            foreach (var group in groups)
            {
                if (group.Value.Count < 2 || !group.Value.Any(x => x.Type == BsonExpressionType.Or)) continue;
                var candidate = TryBooleanRangeIndex(group.Key, group.Value);
                if (candidate == null) continue;
                if (_booleanCoveredTerms == null) _booleanCoveredTerms = new HashSet<BsonExpression>();
                _booleanCoveredTerms.UnionWith(group.Value);
                if (best == null || candidate.Cost < best.Cost) best = candidate;
            }
            return best;
        }

        private static bool ValidateBooleanRanges(BsonExpression expression, ref BsonExpression field, ref int budget)
        {
            if (--budget < 0) return false;
            if (expression.Type == BsonExpressionType.And || expression.Type == BsonExpressionType.Or)
                return ValidateBooleanRanges(expression.Left, ref field, ref budget) && ValidateBooleanRanges(expression.Right, ref field, ref budget);
            if (!TryGetUnionConstraint(expression, out var current, out var value, out _) ||
                !IndexExpressionIdentity.IsMemberPath(current) || !IsRangeValue(value, ref budget)) return false;
            if (field != null && !IndexExpressionIdentity.Matches(field.Source, current)) return false;
            field = current;
            return true;
        }

        private IndexCost TryBooleanRangeIndex(CollectionIndex index, List<BsonExpression> expressions)
        {
            try
            {
                var constraint = new ScalarIndexConstraint(_collation);
                List<BsonExpression> nested = null;
                foreach (var expression in expressions)
                {
                    if (!CollectBooleanConjunction(BooleanSource(expression), constraint, ref nested)) return null;
                }
                var ranges = FinishBooleanConjunction(constraint, nested, null);
                if (ranges == null) return null;
                return new IndexCost(index, expressions[0], IndexRangeUnion.FromNormalized(index.Name, ranges), expressions, scalarKeys: true);
            }
            catch (Exception)
            {
                // All leaves were validated before evaluation. Throwing arithmetic
                // or invalid bindings still belong to the original filter.
                return null;
            }
        }

        private List<ScalarBounds> EvaluateBooleanRanges(BsonExpression expression, List<ScalarBounds> context)
        {
            if (expression.Type == BsonExpressionType.Or)
            {
                var left = EvaluateBooleanRanges(expression.Left, context);
                var right = EvaluateBooleanRanges(expression.Right, context);
                return left == null || right == null ? null : ScalarIntervals.Union(left, right, _collation);
            }
            var constraint = new ScalarIndexConstraint(_collation);
            List<BsonExpression> nested = null;
            if (!CollectBooleanConjunction(expression, constraint, ref nested)) return null;
            return FinishBooleanConjunction(constraint, nested, context);
        }

        private List<ScalarBounds> FinishBooleanConjunction(ScalarIndexConstraint constraint,
            List<BsonExpression> nested, List<ScalarBounds> context)
        {
            var ranges = constraint.BoundRanges(context);
            // Apply scalar bounds and sibling conditions before expanding IN
            // values. The whole shape was proven pure before reading bindings.
            if (nested != null)
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    foreach (var item in nested)
                    {
                        if (HasBooleanMembership(item) != (pass == 1)) continue;
                        ranges = EvaluateBooleanRanges(item, ranges);
                        if (ranges == null) return null;
                        // Even an empty context must evaluate subsequent bounds:
                        // invalid/throwing bindings still require filter fallback.
                    }
                }
            }
            return constraint.IntersectRanges(ranges);
        }

        private static bool HasBooleanMembership(BsonExpression expression) =>
            expression.Type == BsonExpressionType.And || expression.Type == BsonExpressionType.Or
                ? HasBooleanMembership(expression.Left) || HasBooleanMembership(expression.Right)
                : expression.Type == BsonExpressionType.In || expression.IsANY;

        private bool CollectBooleanConjunction(BsonExpression expression, ScalarIndexConstraint constraint, ref List<BsonExpression> nested)
        {
            if (expression.Type == BsonExpressionType.And)
                return CollectBooleanConjunction(expression.Left, constraint, ref nested) && CollectBooleanConjunction(expression.Right, constraint, ref nested);
            if (expression.Type == BsonExpressionType.Or)
            {
                if (nested == null) nested = new List<BsonExpression>();
                nested.Add(expression);
                return true;
            }
            TryGetUnionConstraint(expression, out _, out var value, out var operation);
            var current = value.IsScalar ? value.ExecuteScalar(_collation) : new BsonArray(value.Execute(_collation));
            return constraint.Intersect(operation, current);
        }
    }
}
