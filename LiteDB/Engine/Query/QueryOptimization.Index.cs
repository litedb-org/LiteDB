using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class QueryOptimization
    {
        /// <summary>
        /// Try select index based on lowest cost or GroupBy/OrderBy reuse - use this priority order:
        /// - Get lowest index cost used in WHERE expressions (will filter data)
        /// - If there is no candidate, try get:
        ///     - Same of GroupBy
        ///     - Same of OrderBy
        ///     - Prefered single-field (when no lookup neeed)
        /// </summary>
        private IndexCost ChooseIndex(HashSet<string> fields)
        {
            var indexes = _snapshot.CollectionPage.GetCollectionIndexes().Where(x => x.IndexType == 0).ToArray();

            // if query contains a single field used, give preferred if this index exists
            var preferred = fields.Count == 1 ? "$." + fields.First() : null;

            // otherwise, check for lowest index cost
            IndexCost lowest = this.ChooseDisjunctionIndex(indexes);
            var combined = this.ChooseConstraintIndex(indexes, out var covered);
            if (combined != null && (lowest == null || combined.Cost <= lowest.Cost)) lowest = combined;

            // test all possible predicates in terms
            foreach (var expr in _terms.Where(x => x.IsPredicate))
            {
                if (covered?.Contains(expr) == true) continue;
                ENSURE(expr.Left != null && expr.Right != null, "predicate expression must has left/right expressions");

                Tuple<CollectionIndex, BsonExpression> index = null;

                // check if expression is ANY
                if (expr.Left.IsScalar == false && expr.Right.IsScalar == true)
                {
                    // ANY expression support only LEFT (Enum) -> RIGHT (Scalar)
                    if (expr.IsANY)
                    {
                        index = indexes
                            .Where(x => x.Expression == expr.Left.Source && expr.Right.IsValue)
                            .Select(x => Tuple.Create(x, expr.Right))
                            .FirstOrDefault();
                    }
                    // ALL are not supported in index
                }
                else
                {
                    index = indexes
                        .Where(x => x.Expression == expr.Left.Source && expr.Right.IsValue)
                        .Select(x => Tuple.Create(x, expr.Right))
                        .Union(indexes
                            .Where(x => x.Expression == expr.Right.Source && expr.Left.IsValue)
                            .Select(x => Tuple.Create(x, expr.Left))
                        ).FirstOrDefault();
                }

                // get index that match with expression left/right side

                if (index == null) continue;

                // calculate index score and store highest score
                var current = new IndexCost(index.Item1, expr, index.Item2, _collation);

                if (lowest == null || current.Cost < lowest.Cost)
                {
                    lowest = current;
                }
            }

            // if no index found, try use same index in orderby/groupby/preferred
            if (lowest == null && (_query.OrderBy.Count > 0 || _query.GroupBy != null || preferred != null))
            {
                var orderByExpr = _query.OrderBy.Count > 0 ? _query.OrderBy[0].Expression.Source : null;
                var index =
                    indexes.FirstOrDefault(x => x.Expression == _query.GroupBy?.Source) ??
                    indexes.FirstOrDefault(x => x.Expression == orderByExpr) ??
                    indexes.FirstOrDefault(x => x.Expression == preferred);

                if (index != null)
                {
                    lowest = new IndexCost(index);
                }
            }

            return lowest;
        }

    }
}
