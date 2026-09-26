using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Class that optimize query transforming user "Query" into "QueryPlan"
    /// </summary>
    internal partial class QueryOptimization
    {
        private readonly Snapshot _snapshot;
        private readonly Query _query;
        private readonly Collation _collation;
        private readonly bool _indexesOrdered;
        private readonly QueryPlan _queryPlan;
        private readonly List<BsonExpression> _terms = new List<BsonExpression>();
        private bool _vectorOrderConsumed;
        private bool _vectorPrimaryOrderMatched;

        public QueryOptimization(Snapshot snapshot, Query query, IEnumerable<BsonDocument> source, Collation collation, bool indexesOrdered = true)
        {
            if (query.Select == null) throw new ArgumentNullException(nameof(query.Select));

            _snapshot = snapshot;
            _query = query;
            _collation = collation;
            _indexesOrdered = indexesOrdered;

            _queryPlan = new QueryPlan(snapshot.CollectionName)
            {
                // define index only if source are external collection
                Index = source != null ? new IndexVirtual(source) : null,
                Select = new Select(_query.Select, _query.Select.UseSource),
                ForUpdate = query.ForUpdate,
                Limit = query.Limit,
                Offset = query.Offset,
                VectorScore = query.VectorScore
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

            // make the ownership boundary explicit in the physical plan
            this.DefineBorrowedExecution();

            this.DefineRowAggregate();

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
                    if (TryEvaluateBoolean(predicate.Right, out var right) && !right)
                    {
                        _terms.Add(predicate); // Preserve evaluation of the left before false.
                        return;
                    }
                    var left = predicate.Left;
                    var rhs = predicate.Right;

                    add(left);
                    add(rhs);
                }
                else
                {
                    throw LiteException.InvalidExpressionTypePredicate(predicate);
                }
            }

            // check all where predicate for AND operators
            foreach(var original in _query.Where)
            {
                if (original.UseSource) { add(original); continue; }
                var predicate = this.SimplifyPredicate(original, out var constant);
                if (!constant.HasValue) add(predicate);
                else if (!constant.Value)
                {
                    if (_terms.Count == 0) _constantFalse = true;
                    else _terms.Add(predicate);
                }
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
                    _terms[i] = this.NormalizeContainsTerm(term);
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
            IReadOnlyCollection<BsonExpression> consumed = null;

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
                    consumed = indexCost?.ConsumedExpressions;
                }
            }
            else
            {
                ENSURE(_queryPlan.Index is IndexVirtual, "pre-defined index must be only for virtual collections");

                _queryPlan.IndexCost = 0;
            }

            // if is only 1 field to deserialize and this field are same as index, use IndexKeyOnly = rue
            // Legacy-ordered keys may also be stale, so a LegacyIndexScan reads documents.
            if (_indexesOrdered && !(_queryPlan.Index is VectorIndexQuery) && _queryPlan.Fields.Count == 1 && IsFieldIndex(_queryPlan.IndexExpression, _queryPlan.Fields.First()))
            {
                // best choice - no need lookup for document (use only index)
                _queryPlan.IsIndexKeyOnly = true;
            }

            // fill filter using all expressions (remove selected term used in Index)
            _queryPlan.Filters.AddRange(_terms.Where(x => x != selected && (consumed == null || !consumed.Contains(x))));
            if (_constantFalse) this.UseEmptyInput();
        }

        #endregion

        #region OrderBy / GroupBy Definition

        /// <summary>
        /// Define OrderBy optimization (try re-use index)
        /// </summary>
        private void DefineOrderBy()
        {
            if (_query.OrderBy.Count == 0)
            {
                // Unbounded WhereNear preserves metric ranking through the normal sorter.
                if (_query.GroupBy == null && _queryPlan.Index is VectorIndexQuery vector && vector.RequiresSort)
                {
                    _queryPlan.OrderBy = new OrderBy(new[] { vector.CreateOrderByItem(Query.Ascending) });
                }
                return;
            }

            var segments = _query.OrderBy.Select(x => new OrderByItem(x.Expression, x.Order)).ToArray();
            if (_vectorOrderConsumed) return;

            if (_vectorPrimaryOrderMatched)
            {
                // Retain the metric score as the primary key so ThenBy only breaks score ties.
                // Re-evaluating VECTOR_SIM here would replace Euclidean/dot-product scores with cosine.
                var index = (VectorIndexQuery)_queryPlan.Index;
                segments[0] = index.CreateOrderByItem(segments[0].Order);
            }

            var orderBy = new OrderBy(segments);

            // if index expression are same as primary OrderBy segment, use index order configuration
            if (_indexesOrdered && !orderBy.PrimaryExpression.RequiresExactSort && !(_queryPlan.Index is VectorIndexQuery) &&
                MatchesStoredIndex(_queryPlan.IndexExpression, orderBy.PrimaryExpression))
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

            if (_query.Includes.Count > 0) throw new NotSupportedException("GROUP BY expression do not support INCLUDE");

            var expression = _query.GroupBy;
            var select = _queryPlan.Select.Expression;
            // SQL SELECT collects enumerable expressions into one array per group.
            // Match that behavior for fluent SELECT * without changing the caller's query.
            if (!select.IsScalar) select = BsonExpression.Create("ARRAY(" + select.Source + ")", select.Parameters);
            var having = _query.Having;
            var groupOrderBy = (OrderBy)null;

            // if groupBy use same expression in index, no additional ordering is required before grouping
            if (_indexesOrdered && !(_queryPlan.Index is VectorIndexQuery) && IndexExpressionIdentity.Matches(_queryPlan.IndexExpression, expression))
            {
                // index already provides grouped ordering
            }
            else
            {
                // create orderBy expression
                groupOrderBy = new OrderBy(new[] { new OrderByItem(expression, Query.Ascending) });
            }

            _queryPlan.GroupBy = new GroupBy(expression, select, having, groupOrderBy);
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
