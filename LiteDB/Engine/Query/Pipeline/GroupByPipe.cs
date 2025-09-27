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
            var result = this.SelectGroupBy(groups, query.GroupBy);

            if (query.OrderBy != null)
            {
                result = this.OrderGroupedResult(result, query.OrderBy, query.Offset, query.Limit);
            }
            else
            {
                // apply offset
                if (query.Offset > 0) result = result.Skip(query.Offset);

                // apply limit
                if (query.Limit < int.MaxValue) result = result.Take(query.Limit);
            }

            return result;
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

                    groupBy.Select.Parameters["key"] = key;

                    var group = YieldDocuments(key, enumerator, groupBy, done);

                    yield return new DocumentCacheEnumerable(group, _lookup);
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
                    groupBy.Select.Parameters["key"] = current;

                    // stop current sequence
                    yield break;
                }
            }
        }

        /// <summary>
        /// Run Select expression over a group source - each group will return a single value
        /// If contains Having expression, test if result = true before run Select
        /// </summary>
        private IEnumerable<BsonDocument> SelectGroupBy(IEnumerable<DocumentCacheEnumerable> groups, GroupBy groupBy)
        {
            var defaultName = groupBy.Select.DefaultFieldName();

            foreach (var group in groups)
            {
                // transfom group result if contains select expression
                BsonValue value;

                try
                {
                    if (groupBy.Having != null)
                    {
                        var filter = groupBy.Having.ExecuteScalar(group, null, null, _pragmas.Collation);

                        if (!filter.IsBoolean || !filter.AsBoolean) continue;
                    }

                    value = groupBy.Select.ExecuteScalar(group, null, null, _pragmas.Collation);
                }
                finally
                {
                    group.Dispose();
                }

                if (value.IsDocument)
                {
                    yield return value.AsDocument;
                }
                else
                {
                    yield return new BsonDocument { [defaultName] = value };
                }
            }
        }

        /// <summary>
        /// Apply ORDER BY over grouped projection using in-memory sorting.
        /// </summary>
        private IEnumerable<BsonDocument> OrderGroupedResult(IEnumerable<BsonDocument> source, OrderBy orderBy, int offset, int limit)
        {
            var segments = orderBy.Segments;
            var orders = segments.Select(x => x.Order).ToArray();
            var buffer = new List<(SortKey Key, BsonDocument Document)>();

            foreach (var document in source)
            {
                var values = new BsonValue[segments.Count];

                for (var i = 0; i < segments.Count; i++)
                {
                    values[i] = segments[i].Expression.ExecuteScalar(document, _pragmas.Collation);
                }

                var key = SortKey.FromValues(values, orders);

                buffer.Add((key, document));
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

                yield return item.Document;

                returned++;

                if (limit != int.MaxValue && returned >= limit)
                {
                    yield break;
                }
            }
        }
    }
}