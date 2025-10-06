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

            // run orderBy used to prepare data for grouping (if not already ordered by index)
            if (query.GroupBy.OrderBy != null)
            {
                source = this.OrderBy(source, query.GroupBy.OrderBy, 0, int.MaxValue);
            }

            // apply groupby
            var groups = this.GroupBy(source, query.GroupBy);

            // apply group filter and transform result
            var result = this.SelectGroupBy(groups, query.GroupBy, query.OrderBy);

            if (query.OrderBy != null)
            {
                return this.OrderGroupedResult(result, query.OrderBy, query.Offset, query.Limit)
                    .Select(x => x.Document);
            }

            // apply offset
            if (query.Offset > 0) result = result.Skip(query.Offset);

            // apply limit
            if (query.Limit < int.MaxValue) result = result.Take(query.Limit);

            return result.Select(x => x.Document);
        }

        /// <summary>
        /// GROUP BY: Apply groupBy expression and aggregate results in DocumentGroup
        /// </summary>
        private readonly struct GroupSource
        {
            public GroupSource(BsonValue key, DocumentCacheEnumerable documents)
            {
                this.Key = key;
                this.Documents = documents;
            }

            public BsonValue Key { get; }

            public DocumentCacheEnumerable Documents { get; }
        }

        private IEnumerable<GroupSource> GroupBy(IEnumerable<BsonDocument> source, GroupBy groupBy)
        {
            using (var enumerator = source.GetEnumerator())
            {
                var done = new Done { Running = enumerator.MoveNext() };

                while (done.Running)
                {
                    var key = groupBy.Expression.ExecuteScalar(enumerator.Current, _pragmas.Collation);

                    var group = YieldDocuments(key, enumerator, groupBy, done);

                    yield return new GroupSource(key, new DocumentCacheEnumerable(group, _lookup));
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

                if (_pragmas.Collation.Equals(key, current))
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
        private readonly struct GroupedResult
        {
            public GroupedResult(BsonValue key, BsonDocument document, BsonValue[] orderValues)
            {
                this.Key = key;
                this.Document = document;
                this.OrderValues = orderValues;
            }

            public BsonValue Key { get; }

            public BsonDocument Document { get; }

            public BsonValue[] OrderValues { get; }
        }

        private static void SetKeyParameter(BsonExpression expression, BsonValue key)
        {
            if (expression?.Parameters != null)
            {
                expression.Parameters["key"] = key;
            }
        }

        private IEnumerable<GroupedResult> SelectGroupBy(IEnumerable<GroupSource> groups, GroupBy groupBy, OrderBy resultOrderBy)
        {
            var defaultName = groupBy.Select.DefaultFieldName();

            foreach (var group in groups)
            {
                var key = group.Key;
                var cache = group.Documents;

                SetKeyParameter(groupBy.Select, key);
                SetKeyParameter(groupBy.Having, key);

                BsonDocument document = null;
                BsonValue[] orderValues = null;

                try
                {
                    if (groupBy.Having != null)
                    {
                        var filter = groupBy.Having.ExecuteScalar(cache, null, null, _pragmas.Collation);

                        if (!filter.IsBoolean || !filter.AsBoolean)
                        {
                            continue;
                        }
                    }

                    BsonValue value;

                    if (ReferenceEquals(groupBy.Select, BsonExpression.Root))
                    {
                        var items = new BsonArray();

                        foreach (var groupDocument in cache)
                        {
                            items.Add(groupDocument);
                        }

                        value = new BsonDocument
                        {
                            [LiteGroupingFieldNames.Key] = key,
                            [LiteGroupingFieldNames.Items] = items
                        };
                    }
                    else
                    {
                        value = groupBy.Select.ExecuteScalar(cache, null, null, _pragmas.Collation);
                    }

                    if (value.IsDocument)
                    {
                        document = value.AsDocument;
                    }
                    else
                    {
                        document = new BsonDocument { [defaultName] = value };
                    }

                    if (resultOrderBy != null)
                    {
                        var segments = resultOrderBy.Segments;

                        orderValues = new BsonValue[segments.Count];

                        for (var i = 0; i < segments.Count; i++)
                        {
                            var expression = segments[i].Expression;

                            SetKeyParameter(expression, key);

                            orderValues[i] = expression.ExecuteScalar(cache, document, null, _pragmas.Collation);
                        }
                    }
                }
                finally
                {
                    cache.Dispose();
                }

                yield return new GroupedResult(key, document, orderValues);
            }
        }

        /// <summary>
        /// Apply ORDER BY over grouped projection using in-memory sorting.
        /// </summary>
        private IEnumerable<GroupedResult> OrderGroupedResult(IEnumerable<GroupedResult> source, OrderBy orderBy, int offset, int limit)
        {
            var segments = orderBy.Segments;
            var orders = segments.Select(x => x.Order).ToArray();
            var buffer = new List<(SortKey Key, GroupedResult Result)>();

            foreach (var item in source)
            {
                var values = item.OrderValues ?? new BsonValue[segments.Count];

                if (item.OrderValues == null)
                {
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var expression = segments[i].Expression;

                        SetKeyParameter(expression, item.Key);

                        values[i] = expression.ExecuteScalar(item.Document, _pragmas.Collation);
                    }
                }

                var key = SortKey.FromValues(values, orders);

                buffer.Add((key, item));
            }

            buffer.Sort((left, right) => left.Key.CompareTo(right.Key, _pragmas.Collation));

            var skipped = 0;
            var returned = 0;

            foreach (var item in buffer)
            {
                if (skipped < offset)
                {
                    skipped++;
                    continue;
                }

                yield return item.Result;

                returned++;

                if (limit != int.MaxValue && returned >= limit)
                {
                    yield break;
                }
            }
        }
    }
}