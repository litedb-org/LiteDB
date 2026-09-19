namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private IndexCost ChooseDisjunctionIndex(CollectionIndex[] indexes)
        {
            IndexCost best = ChooseBooleanIntersectionIndex(indexes);
            foreach (var term in _terms)
            {
                if (term.Type != BsonExpressionType.Or) continue;
                if (_booleanCoveredTerms?.Contains(term) == true) continue;
                BsonExpression keyExpression = null;
                if (!ValidateEqualities(term, ref keyExpression))
                {
                    var guard = ChooseRangeDisjunctionIndex(term, indexes) ?? ChooseCommonDisjunctionIndex(term, indexes);
                    if (guard != null && (best == null || guard.Cost < best.Cost)) best = guard;
                    continue;
                }
                var index = FindStoredIndex(indexes, keyExpression);
                if (index == null) continue;

                // Values belong to the current query invocation. Never cache a physical
                // plan or mutate the reusable expression tree while normalizing it.
                var keys = new BsonArray();
                AppendEqualityKeys(term, keys);
                var candidate = new IndexCost(index, term, new IndexIn(index.Name, keys, Query.Ascending), scalarKeys: true);
                if (best == null || candidate.Cost < best.Cost) best = candidate;
            }
            return best;
        }

        private static bool ValidateEqualities(BsonExpression expression, ref BsonExpression keyExpression)
        {
            if (expression.Type == BsonExpressionType.Or)
            {
                return ValidateEqualities(expression.Left, ref keyExpression) &&
                    ValidateEqualities(expression.Right, ref keyExpression);
            }
            if (expression.Type != BsonExpressionType.Equal) return false;
            var field = expression.Left;
            var value = expression.Right;
            if (field.IsValue) { field = expression.Right; value = expression.Left; }
            if (!field.IsScalar || !field.IsImmutable || field.IsValue || !IsStableValue(value)) return false;
            if (keyExpression != null && !IndexExpressionIdentity.Matches(keyExpression.Source, field)) return false;
            keyExpression = field;
            return true;
        }

        private void AppendEqualityKeys(BsonExpression expression, BsonArray keys)
        {
            if (expression.Type == BsonExpressionType.Or)
            {
                AppendEqualityKeys(expression.Left, keys);
                AppendEqualityKeys(expression.Right, keys);
                return;
            }
            var value = expression.Left.IsValue ? expression.Left : expression.Right;
            keys.Add(value.ExecuteScalar(_collation));
        }

        private static bool IsStableValue(BsonExpression expression)
        {
            return expression.IsScalar && expression.IsValue && !expression.UseSource && !expression.IsVolatile &&
                (expression.IsImmutable || expression.Type == BsonExpressionType.Parameter);
        }
    }
}
