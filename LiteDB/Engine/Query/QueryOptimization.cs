using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Class that optimize query transforming user "Query" into "QueryPlan"
    /// </summary>
    internal class QueryOptimization
    {
        private readonly Snapshot _snapshot;
        private readonly Query _query;
        private readonly Collation _collation;
        private readonly QueryPlan _queryPlan;
        private readonly List<BsonExpression> _terms = new List<BsonExpression>();
        private bool _vectorOrderConsumed;

        public QueryOptimization(Snapshot snapshot, Query query, IEnumerable<BsonDocument> source, Collation collation)
        {
            if (query.Select == null) throw new ArgumentNullException(nameof(query.Select));

            _snapshot = snapshot;
            _query = query;
            _collation = collation;

            _queryPlan = new QueryPlan(snapshot.CollectionName)
            {
                // define index only if source are external collection
                Index = source != null ? new IndexVirtual(source) : null,
                Select = new Select(_query.Select, _query.Select.UseSource),
                ForUpdate = query.ForUpdate,
                Limit = query.Limit,
                Offset = query.Offset
            };
        }

        /// <summary>
        /// Build QueryPlan instance based on QueryBuilder fields
        /// - Load used fields in all expressions
        /// - Select best index option
        /// - Fill includes 
        /// - Define orderBy
        /// - Define groupBy
        /// </summary>
        public QueryPlan ProcessQuery()
        {
            // split where expressions into TERMs (splited by AND operator)
            this.SplitWherePredicateInTerms();

            // do terms optimizations
            this.OptimizeTerms();

            // define Fields
            this.DefineQueryFields();

            // define Index, IndexCost, IndexExpression, IsIndexKeyOnly + Where (filters - index)
            this.DefineIndex();

            // define OrderBy
            this.DefineOrderBy();

            // define GroupBy
            this.DefineGroupBy();

            // define IncludeBefore + IncludeAfter
            this.DefineIncludes();

            return _queryPlan;
        }

        #region Split Where

        /// <summary>
        /// Fill terms from where predicate list
        /// </summary>
        private void SplitWherePredicateInTerms()
        {
            void add(BsonExpression predicate)
            {
                // do not accept source * in WHERE
                if (predicate.UseSource)
                {
                    throw new LiteException(0, $"WHERE filter can not use `*` expression in `{predicate.Source}");
                }

                // add expression in where list breaking AND statments
                if (predicate.IsPredicate || predicate.Type == BsonExpressionType.Or)
                {
                    _terms.Add(predicate);
                }
                else if (predicate.Type == BsonExpressionType.And)
                {
                    var left = predicate.Left;
                    var right = predicate.Right;

                    add(left);
                    add(right);
                }
                else
                {
                    throw LiteException.InvalidExpressionTypePredicate(predicate);
                }
            }

            // check all where predicate for AND operators
            foreach(var predicate in _query.Where)
            {
                add(predicate);
            }
        }

        /// <summary>
        /// Do some pre-defined optimization on terms to convert expensive filter in indexable filter
        /// </summary>
        private void OptimizeTerms()
        {
            // simple optimization
            for (var i = 0; i < _terms.Count; i++)
            {
                var term = _terms[i];

                // convert: { [Enum] ANY = [Path] } to { [Path] IN ARRAY([Enum]) }
                // very used in LINQ expressions: `query.Where(x => ids.Contains(x.Id))`
                if (term.Left?.IsScalar == false &&
                    term.IsANY &&
                    term.Type == BsonExpressionType.Equal &&
                    term.Right?.Type == BsonExpressionType.Path)
                {
                    _terms[i] = BsonExpression.Create(term.Right.Source + " IN ARRAY(" + term.Left.Source + ")", term.Parameters);
                }
            }
        }

        #endregion

        #region Document Fields

        /// <summary>
        /// Load all fields that must be deserialize from document.
        /// </summary>
        private void DefineQueryFields()
        {
            // load only query fields (null return all document)
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // include all fields detected in all used expressions
            fields.AddRange(_query.Select.Fields);
            fields.AddRange(_terms.SelectMany(x => x.Fields));
            fields.AddRange(_query.Includes.SelectMany(x => x.Fields));
            fields.AddRange(_query.GroupBy?.Fields);
            fields.AddRange(_query.Having?.Fields);
            if (_query.OrderBy.Count > 0)
            {
                fields.AddRange(_query.OrderBy.SelectMany(x => x.Expression.Fields));
            }

            // if contains $, all fields must be deserialized
            if (fields.Contains("$"))
            {
                fields.Clear();
            }

            _queryPlan.Fields = fields;
        }

        #endregion

        #region Index Definition

        private void DefineIndex()
        {
            // selected expression to be used as index (from _terms)
            BsonExpression selected = null;

            // if index are not defined yet, get index
            if (_queryPlan.Index == null)
            {
                if (this.TrySelectVectorIndex(out var vectorIndex, out selected))
                {
                    _queryPlan.Index = vectorIndex;
                    _queryPlan.IndexCost = vectorIndex.GetCost(null);
                    _queryPlan.IndexExpression = vectorIndex.Expression;
                }
                else
                {
                    // try select best index (if return null, there is no good choice)
                    var indexCost = this.ChooseIndex(_queryPlan.Fields);

                    // if found an index, use-it
                    if (indexCost != null)
                    {
                        _queryPlan.Index = indexCost.Index;
                        _queryPlan.IndexCost = indexCost.Cost;
                        _queryPlan.IndexExpression = indexCost.IndexExpression;
                    }
                    else
                    {
                        // if has no index to use, use full scan over _id
                        var pk = _snapshot.CollectionPage.PK;

                        _queryPlan.Index = new IndexAll("_id", Query.Ascending);
                        _queryPlan.IndexCost = _queryPlan.Index.GetCost(pk);
                        _queryPlan.IndexExpression = "$._id";
                    }

                    // get selected expression used as index
                    selected = indexCost?.Expression;
                }
            }
            else
            {
                ENSURE(_queryPlan.Index is IndexVirtual, "pre-defined index must be only for virtual collections");

                _queryPlan.IndexCost = 0;
            }

            // if is only 1 field to deserialize and this field are same as index, use IndexKeyOnly = rue
            if (_queryPlan.Fields.Count == 1 && _queryPlan.IndexExpression == "$." + _queryPlan.Fields.First())
            {
                // best choice - no need lookup for document (use only index)
                _queryPlan.IsIndexKeyOnly = true;
            }

            // fill filter using all expressions (remove selected term used in Index)
            _queryPlan.Filters.AddRange(_terms.Where(x => x != selected));
        }

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
            var indexes = _snapshot.CollectionPage.GetCollectionIndexes().ToArray();

            // if query contains a single field used, give preferred if this index exists
            var preferred = fields.Count == 1 ? "$." + fields.First() : null;

            // otherwise, check for lowest index cost
            IndexCost lowest = null;

            // test all possible predicates in terms
            foreach (var expr in _terms.Where(x => x.IsPredicate))
            {
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

        private bool TrySelectVectorIndex(out VectorIndexQuery index, out BsonExpression consumedTerm)
        {
            index = null;
            consumedTerm = null;

            string expression = null;
            float[] target = null;
            double maxDistance = double.MaxValue;
            var matchedFromOrderBy = false;

            foreach (var term in _terms)
            {
                if (this.TryParseVectorPredicate(term, out expression, out target, out maxDistance))
                {
                    consumedTerm = term;
                    break;
                }
            }

            if (expression == null && _query.OrderBy != null)
            {
                if (this.TryParseVectorExpression(_query.OrderBy, out expression, out target))
                {
                    matchedFromOrderBy = true;
                    maxDistance = double.MaxValue;
                }
            }

            if (expression == null && _query.VectorTarget != null && _query.VectorField != null)
            {
                expression = NormalizeVectorField(_query.VectorField);
                target = _query.VectorTarget?.ToArray();
                maxDistance = _query.VectorMaxDistance;
                matchedFromOrderBy = matchedFromOrderBy || (_query.OrderBy != null && _query.OrderBy.Type == BsonExpressionType.VectorSim);
            }

            if (expression == null || target == null)
            {
                return false;
            }

            int? limit = _query.Limit != int.MaxValue ? _query.Limit : (int?)null;

            foreach (var (candidate, metadata) in _snapshot.CollectionPage.GetVectorIndexes())
            {
                if (!string.Equals(candidate.Expression, expression, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (metadata.Dimensions != target.Length)
                {
                    continue;
                }

                index = new VectorIndexQuery(candidate.Name, _snapshot, candidate, metadata, target, maxDistance, limit, _collation);

                if (matchedFromOrderBy)
                {
                    _vectorOrderConsumed = true;
                }

                return true;
            }

            return false;
        }

        private bool TryParseVectorPredicate(BsonExpression predicate, out string expression, out float[] target, out double maxDistance)
        {
            expression = null;
            target = null;
            maxDistance = double.NaN;

            if (predicate == null)
            {
                return false;
            }

            if ((predicate.Type == BsonExpressionType.LessThan || predicate.Type == BsonExpressionType.LessThanOrEqual) &&
                this.TryParseVectorExpression(predicate.Left, out expression, out target) &&
                TryConvertToDouble(predicate.Right?.ExecuteScalar(_collation), out maxDistance))
            {
                return true;
            }

            if ((predicate.Type == BsonExpressionType.GreaterThan || predicate.Type == BsonExpressionType.GreaterThanOrEqual) &&
                this.TryParseVectorExpression(predicate.Right, out expression, out target) &&
                TryConvertToDouble(predicate.Left?.ExecuteScalar(_collation), out maxDistance))
            {
                return true;
            }

            expression = null;
            target = null;
            maxDistance = double.NaN;
            return false;
        }

        private bool TryParseVectorExpression(BsonExpression expression, out string fieldExpression, out float[] target)
        {
            fieldExpression = null;
            target = null;

            if (expression == null || expression.Type != BsonExpressionType.VectorSim)
            {
                return false;
            }

            var field = expression.Left;
            if (field == null || string.IsNullOrEmpty(field.Source))
            {
                return false;
            }

            var targetValue = expression.Right?.ExecuteScalar(_collation);

            if (!TryConvertToVector(targetValue, out target))
            {
                return false;
            }

            fieldExpression = field.Source;
            return true;
        }

        private static bool TryConvertToVector(BsonValue value, out float[] vector)
        {
            vector = null;

            if (value == null || value.IsNull)
            {
                return false;
            }

            if (value.Type == BsonType.Vector)
            {
                vector = value.AsVector.ToArray();
                return true;
            }

            if (!value.IsArray)
            {
                return false;
            }

            var array = value.AsArray;
            var buffer = new float[array.Count];

            for (var i = 0; i < array.Count; i++)
            {
                var item = array[i];

                if (item.IsNull)
                {
                    return false;
                }

                try
                {
                    buffer[i] = (float)item.AsDouble;
                }
                catch
                {
                    return false;
                }
            }

            vector = buffer;
            return true;
        }

        private static bool TryConvertToDouble(BsonValue value, out double number)
        {
            number = double.NaN;

            if (value == null || value.IsNull || !value.IsNumber)
            {
                return false;
            }

            number = value.AsDouble;
            return !double.IsNaN(number);
        }

        private static string NormalizeVectorField(string field)
        {
            if (string.IsNullOrWhiteSpace(field))
            {
                return field;
            }

            field = field.Trim();

            if (field.StartsWith("$", StringComparison.Ordinal))
            {
                return field;
            }

            if (field.StartsWith(".", StringComparison.Ordinal))
            {
                field = field.Substring(1);
            }

            return "$." + field;
        }

        #endregion

        #region OrderBy / GroupBy Definition

        /// <summary>
        /// Define OrderBy optimization (try re-use index)
        /// </summary>
        private void DefineOrderBy()
        {
            // if has no order by, returns null
            if (_query.OrderBy.Count == 0) return;

            if (_vectorOrderConsumed)
            {
                _queryPlan.OrderBy = null;
                return;
            }

            var orderBy = new OrderBy(_query.OrderBy.Select(x => new OrderByItem(x.Expression, x.Order)));

            // if index expression are same as primary OrderBy segment, use index order configuration
            if (orderBy.PrimaryExpression.Source == _queryPlan.IndexExpression)
            {
                _queryPlan.Index.Order = orderBy.PrimaryOrder;

                if (orderBy.Segments.Count == 1)
                {
                    orderBy = null;
                }
            }

            // otherwise, query.OrderBy will be set according user defined
            _queryPlan.OrderBy = orderBy;
        }

        /// <summary>
        /// Define GroupBy optimization (try re-use index)
        /// </summary>
        private void DefineGroupBy()
        {
            if (_query.GroupBy == null) return;

            if (_query.OrderBy.Count > 0) throw new NotSupportedException("GROUP BY expression do not support ORDER BY");
            if (_query.Includes.Count > 0) throw new NotSupportedException("GROUP BY expression do not support INCLUDE");

            var groupBy = new GroupBy(_query.GroupBy, _queryPlan.Select.Expression, _query.Having);
            var orderBy = (OrderBy)null;

            // if groupBy use same expression in index, set group by order to MaxValue to not run
            if (groupBy.Expression.Source == _queryPlan.IndexExpression)
            {
                // great - group by expression are same used in index - no changes here
            }
            else
            {
                // create orderBy expression
                orderBy = new OrderBy(new[] { new OrderByItem(groupBy.Expression, Query.Ascending) });
            }

            _queryPlan.GroupBy = groupBy;
            _queryPlan.OrderBy = orderBy;
        }

        #endregion

        /// <summary>
        /// Will define each include to be run BEFORE where (worst) OR AFTER where (best)
        /// </summary>
        private void DefineIncludes()
        {
            foreach(var include in _query.Includes)
            {
                // includes always has one single field
                var field = include.Fields.Single();

                // test if field are using in any filter or orderBy
                var used = _queryPlan.Filters.Any(x => x.Fields.Contains(field)) ||
                    (_queryPlan.OrderBy?.ContainsField(field) ?? false);

                if (used)
                {
                    _queryPlan.IncludeBefore.Add(include);
                }

                // in case of using OrderBy this can eliminate IncludeBefre - this need be added in After
                if (!used || _queryPlan.OrderBy != null)
                {
                    _queryPlan.IncludeAfter.Add(include);
                }
            }
        }
    }
}