using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private IndexCost ChooseDisjunctionIndex(CollectionIndex[] indexes)
        {
            IndexCost best = null;
            foreach (var term in _terms)
            {
                if (term.Type != BsonExpressionType.Or) continue;
                var values = new List<BsonExpression>();
                string source = null;
                if (!CollectEqualities(term, ref source, values))
                {
                    var guard = ChooseCommonDisjunctionIndex(term, indexes);
                    if (guard != null && (best == null || guard.Cost < best.Cost)) best = guard;
                    continue;
                }
                var index = indexes.FirstOrDefault(x => x.Expression == source);
                if (index == null) continue;

                // Values belong to the current query invocation. Never cache a physical
                // plan or mutate the reusable expression tree while normalizing it.
                var keys = new BsonArray(values.Select(x => x.ExecuteScalar(_collation)));
                var candidate = new IndexCost(index, term, new IndexIn(index.Name, keys, Query.Ascending), scalarKeys: true);
                if (best == null || candidate.Cost < best.Cost) best = candidate;
            }
            return best;
        }

        private static bool CollectEqualities(BsonExpression expression, ref string source, List<BsonExpression> values)
        {
            if (expression.Type == BsonExpressionType.Or)
            {
                return CollectEqualities(expression.Left, ref source, values) &&
                    CollectEqualities(expression.Right, ref source, values);
            }
            if (expression.Type != BsonExpressionType.Equal) return false;
            var field = expression.Left;
            var value = expression.Right;
            if (field.IsValue) { field = expression.Right; value = expression.Left; }
            if (!field.IsScalar || !field.IsImmutable || field.IsValue || !IsStableValue(value)) return false;
            if (source != null && source != field.Source) return false;
            source = field.Source;
            values.Add(value);
            return true;
        }

        private static bool IsStableValue(BsonExpression expression)
        {
            return expression.IsScalar && expression.IsValue && !expression.UseSource && !expression.IsVolatile &&
                (expression.IsImmutable || expression.Type == BsonExpressionType.Parameter);
        }
    }
}
