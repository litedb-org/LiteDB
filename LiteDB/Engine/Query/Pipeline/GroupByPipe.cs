using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Implement query using GroupBy expression
    /// </summary>
    internal class GroupByPipe : BasePipe
    {
        public GroupByPipe(TransactionService transaction, IDocumentLookup loader, SortDisk tempDisk, EnginePragmas pragmas, uint maxItemsCount)
            : base(transaction, loader, tempDisk, pragmas, maxItemsCount)
        {
        }

        /// <summary>
        /// GroupBy Pipe Order
        /// - LoadDocument
        /// - Filter
        /// - OrderBy (to GroupBy)
        /// - GroupBy
        /// - HavingSelectGroupBy
        /// - OffSet
        /// - Limit
        /// </summary>
        public override IEnumerable<BsonDocument> Pipe(IEnumerable<IndexNode> nodes, QueryPlan query)
        {
            // starts pipe loading document
            var source = this.LoadDocument(nodes);

            // filter results according filter expressions
            foreach (var expr in query.Filters)
            {
                source = this.Filter(source, expr);
            }

            // run orderBy used in GroupBy (if not already ordered by index)
            if (query.OrderBy != null)
            {
                source = this.OrderBy(source, query.OrderBy, 0, int.MaxValue);
            }

            // apply groupby
            var groups = this.GroupBy(source, query.GroupBy);

            // apply group filter and transform result
            var projections = this.SelectGroupBy(groups, query.GroupBy);

            if (query.GroupBy.ProjectionOrderBy != null)
            {
                return this.OrderGroupedResult(projections, query.GroupBy.ProjectionOrderBy, query.Offset, query.Limit);
            }

            if (query.Offset > 0) projections = projections.Skip(query.Offset);

            if (query.Limit < int.MaxValue) projections = projections.Take(query.Limit);

            return projections.Select(x => x.Document);
        }

        /// <summary>
        /// GROUP BY: Apply groupBy expression and aggregate results in DocumentGroup
        /// </summary>
        private IEnumerable<DocumentCacheEnumerable> GroupBy(IEnumerable<BsonDocument> source, GroupBy groupBy)
        {
            using (var enumerator = source.GetEnumerator())
            {
                var done = new Done { Running = enumerator.MoveNext() };

                while (done.Running)
                {
                    var key = groupBy.Expression.ExecuteScalar(enumerator.Current, _pragmas.Collation);

                    var grouping = new DocumentCacheEnumerable(YieldDocuments(key, enumerator, groupBy, done), _lookup)
                    {
                        GroupKey = key
                    };

                    yield return grouping;
                }
            }
        }

        /// <summary>
        /// YieldDocuments will run over all key-ordered source and returns groups of source
        /// </summary>
        private IEnumerable<BsonDocument> YieldDocuments(BsonValue key, IEnumerator<BsonDocument> enumerator, GroupBy groupBy, Done done)
        {
            yield return enumerator.Current;

            while (done.Running = enumerator.MoveNext())
            {
                var current = groupBy.Expression.ExecuteScalar(enumerator.Current, _pragmas.Collation);

                if (key == current)
                {
                    // yield return document in same key (group)
                    yield return enumerator.Current;
                }
                else
                {
                    // stop current sequence
                    yield break;
                }
            }
        }

        /// <summary>
        /// Run Select expression over a group source - each group will return a single value
        /// If contains Having expression, test if result = true before run Select
        /// </summary>
        private IEnumerable<GroupResult> SelectGroupBy(IEnumerable<DocumentCacheEnumerable> groups, GroupBy groupBy)
        {
            var defaultName = groupBy.Select.DefaultFieldName();

            foreach (var group in groups)
            {
                // transfom group result if contains select expression
                BsonValue value;
                List<BsonDocument> documents = null;
                BsonValue key = BsonValue.Null;
                BsonArray groupArray = null;

                try
                {
                    documents = group.ToList();

                    key = group.GroupKey;
                    groupArray = new BsonArray(documents.Cast<BsonValue>());

                    groupBy.Select.Parameters["key"] = key;
                    groupBy.Select.Parameters["group"] = groupArray;

                    if (groupBy.Having != null)
                    {
                        groupBy.Having.Parameters["key"] = key;
                        groupBy.Having.Parameters["group"] = groupArray;
                    }

                    if (groupBy.Having != null)
                    {
                        var filter = groupBy.Having.ExecuteScalar(documents, null, null, _pragmas.Collation);

                        if (!filter.IsBoolean || !filter.AsBoolean) continue;
                    }

                    value = groupBy.Select.ExecuteScalar(documents, null, null, _pragmas.Collation);
                }
                finally
                {
                    group.Dispose();
                }

                if (value.IsDocument)
                {
                    yield return new GroupResult(value.AsDocument, key, groupArray);
                }
                else
                {
                    yield return new GroupResult(new BsonDocument { [defaultName] = value }, key, groupArray);
                }
            }
        }

        private IEnumerable<BsonDocument> OrderGroupedResult(IEnumerable<GroupResult> source, OrderBy orderBy, int offset, int limit)
        {
            var segments = orderBy.Segments;
            var buffer = source.Select(item => (Result: item, Keys: EvaluateKeys(item))).ToList();

            buffer.Sort((left, right) => CompareKeys(left.Keys, right.Keys, segments));

            var result = buffer.Skip(offset);

            if (limit < int.MaxValue)
            {
                result = result.Take(limit);
            }

            foreach (var item in result)
            {
                yield return item.Result.Document;
            }

            BsonValue[] EvaluateKeys(GroupResult item)
            {
                var values = new BsonValue[segments.Count];

                for (var i = 0; i < segments.Count; i++)
                {
                    var expression = segments[i].Expression;

                    expression.Parameters["key"] = item.Key;
                    expression.Parameters["group"] = item.Group;

                    values[i] = expression.ExecuteScalar(item.Document, _pragmas.Collation);
                }

                return values;
            }
        }

        private int CompareKeys(IReadOnlyList<BsonValue> left, IReadOnlyList<BsonValue> right, IReadOnlyList<OrderByItem> segments)
        {
            for (var i = 0; i < segments.Count; i++)
            {
                var result = left[i].CompareTo(right[i], _pragmas.Collation);

                if (result == 0) continue;

                return segments[i].Order == Query.Descending ? -result : result;
            }

            return 0;
        }

        private sealed class GroupResult
        {
            public GroupResult(BsonDocument document, BsonValue key, BsonArray group)
            {
                this.Document = document;
                this.Key = key;
                this.Group = group;
            }

            public BsonDocument Document { get; }

            public BsonValue Key { get; }

            public BsonArray Group { get; }
        }
    }
}
