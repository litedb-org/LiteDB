using System.Collections.Generic;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        private IndexCost ChooseConstraintIndex(CollectionIndex[] indexes, out HashSet<BsonExpression> covered)
        {
            covered = null;
            if (_terms.Count < 2) return null;
            IndexCost best = null;
            foreach (var index in indexes)
            {
                BsonExpression first = null;
                List<BsonExpression> terms = null;
                foreach (var term in _terms)
                {
                    if (!TryGetConstraint(term, out var field, out _, out _) || !IndexExpressionIdentity.Matches(index.Expression, field)) continue;
                    if (first == null) first = term;
                    else
                    {
                        if (terms == null) terms = new List<BsonExpression> { first };
                        terms.Add(term);
                    }
                }
                if (terms == null) continue;
                var constraint = new ScalarIndexConstraint(_collation);
                var valid = true;
                foreach (var term in terms)
                {
                    TryGetConstraint(term, out _, out var value, out var operation);
                    if (!constraint.Intersect(operation, value.ExecuteScalar(_collation))) { valid = false; break; }
                }
                if (!valid) continue;
                var candidate = new IndexCost(index, first, constraint.CreateIndex(index.Name), terms, scalarKeys: true);
                if (covered == null) covered = new HashSet<BsonExpression>();
                covered.UnionWith(terms);
                if (best == null || candidate.Cost < best.Cost) best = candidate;
            }
            return best;
        }

        private static bool TryGetConstraint(BsonExpression expression, out BsonExpression field,
            out BsonExpression value, out BsonExpressionType operation)
        {
            if (TryGetScalarBound(expression, out field, out value, out operation)) return true;
            field = expression.Left;
            value = expression.Right;
            operation = expression.Type;
            if (operation != BsonExpressionType.In && operation != BsonExpressionType.Between) return false;
            return field.IsScalar && field.IsImmutable && !field.IsVolatile && !field.IsValue &&
                value.IsScalar && value.IsValue && !value.UseSource && !value.IsVolatile;
        }
    }
}
