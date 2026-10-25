using System;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private IndexCost ChooseCommonDisjunctionIndex(BsonExpression expression, CollectionIndex[] indexes)
        {
            BsonExpression keyExpression = null;
            BsonValue key = null;
            var budget = 64;
            if (!TryCommonLeadingEquality(expression, ref keyExpression, ref key, ref budget)) return null;
            var index = FindStoredIndex(indexes, keyExpression);
            // This seek enforces a necessary guard, not the entire OR. Retain the
            // original disjunction as a residual filter, with its own bindings.
            return index == null ? null : new IndexCost(index, null, new IndexEquals(index.Name, key), scalarKeys: true);
        }

        private bool TryCommonLeadingEquality(BsonExpression expression, ref BsonExpression keyExpression, ref BsonValue key, ref int budget)
        {
            if (--budget < 0) return false;
            if (expression.Type == BsonExpressionType.Or)
                return TryCommonLeadingEquality(expression.Left, ref keyExpression, ref key, ref budget) &&
                    TryCommonLeadingEquality(expression.Right, ref keyExpression, ref key, ref budget);
            // Restrict extraction to each branch's first condition. Moving a later
            // condition ahead of a throwing/volatile expression can change behavior.
            while (expression.Type == BsonExpressionType.And)
            {
                if (--budget < 0) return false;
                expression = expression.Left;
            }
            if (!TryGetScalarBound(expression, out var field, out var value, out var operation) ||
                operation != BsonExpressionType.Equal || field.Type != BsonExpressionType.Path) return false;
            try
            {
                var current = value.ExecuteScalar(_collation);
                if (ReferenceEquals(current, null)) return false;
                if (keyExpression != null) return IndexExpressionIdentity.Matches(keyExpression.Source, field) && key.CompareTo(current, _collation) == 0;
                keyExpression = field;
                key = current;
                return true;
            }
            catch (Exception)
            {
                // Preserve execution-time errors and short circuits for unsafe values.
                return false;
            }
        }
    }
}
