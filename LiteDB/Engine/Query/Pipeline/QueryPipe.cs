using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Basic query pipe workflow - support filter, includes and orderby
    /// </summary>
    internal class QueryPipe : BasePipe
    {
        public QueryPipe(TransactionService transaction, IDocumentLookup loader, SortDisk tempDisk, EnginePragmas pragmas, uint maxItemsCount)
            : base(transaction, loader, tempDisk, pragmas, maxItemsCount)
        {
        }

        /// <summary>
        /// Query Pipe order
        /// - LoadDocument
        /// - IncludeBefore
        /// - Filter
        /// - OrderBy
        /// - OffSet
        /// - Limit
        /// - IncludeAfter
        /// - Select
        /// </summary>
        public override IEnumerable<BsonDocument> Pipe(IEnumerable<IndexNode> nodes, QueryPlan query)
        {
            var nodeSource = nodes;
            var borrowed = false;
            var datafile = _lookup as DatafileLookup;

            // When the index already determines membership and ordering, skip
            // nodes before loading document payloads. Other plans must paginate
            // after their document filters/includes or in-memory sort.
            var paginateNodes = query.Filters.Count == 0 && query.IncludeBefore.Count == 0 && query.OrderBy == null;
            if (paginateNodes)
            {
                nodeSource = this.SkipNodes(nodeSource, query.Offset);
                if (query.Limit < int.MaxValue) nodeSource = nodeSource.Take(query.Limit);
            }

            // Includes before filtering mutate documents, so only plans without
            // them can move residual predicates ahead of materialization.
            if (datafile != null && query.BorrowedFilter != null)
            {
                nodeSource = this.FilterBorrowed(nodeSource, datafile,
                    query.BorrowedFilter, query.Filters);
                borrowed = true;
            }

            // Count and Exists need no owning source document. Ordering cannot
            // change their result, while offset and limit still can.
            if (query.RowAggregate != null && !query.ForUpdate && query.VectorScore == null &&
                query.IncludeBefore.Count == 0 && query.IncludeAfter.Count == 0 &&
                (query.Filters.Count == 0 || borrowed))
            {
                if (!paginateNodes)
                {
                    if (query.Offset > 0) nodeSource = nodeSource.Skip(query.Offset);
                    if (query.Limit < int.MaxValue) nodeSource = nodeSource.Take(query.Limit);
                }

                return this.AggregateNodes(nodeSource, query.RowAggregate);
            }

            // A direct-field projection owns only its result container and
            // selected values; no source BsonDocument is needed.
            if (query.OrderBy == null && query.IncludeBefore.Count == 0 &&
                query.IncludeAfter.Count == 0 && query.VectorScore == null &&
                !query.Select.All && (query.Filters.Count == 0 || borrowed) &&
                datafile != null && query.BorrowedProjection != null)
            {
                if (!paginateNodes)
                {
                    if (query.Offset > 0) nodeSource = nodeSource.Skip(query.Offset);
                    if (query.Limit < int.MaxValue) nodeSource = nodeSource.Take(query.Limit);
                }

                return this.ProjectBorrowed(nodeSource, datafile, query.BorrowedProjection,
                    query.Select.Expression);
            }

            IEnumerable<BsonDocument> source;

            // Scalar sort keys are the only owning values retained during the
            // sort; source documents are loaded after the final window is known.
            if (query.OrderBy != null && query.IncludeBefore.Count == 0 &&
                (query.Filters.Count == 0 || borrowed) && datafile != null &&
                query.BorrowedOrderBy != null)
            {
                source = this.OrderByBorrowed(nodeSource, datafile, query.OrderBy,
                    query.BorrowedOrderBy, query.Offset, query.Limit);
            }
            else
            {
                // Pagination can run over surviving addresses before an owning
                // document is created when no sort changes their order.
                if (!paginateNodes && borrowed && query.OrderBy == null)
                {
                    if (query.Offset > 0) nodeSource = nodeSource.Skip(query.Offset);
                    if (query.Limit < int.MaxValue) nodeSource = nodeSource.Take(query.Limit);
                }

                source = this.LoadDocument(nodeSource);

                // do includes in result before filter
                foreach (var path in query.IncludeBefore)
                {
                    source = this.Include(source, path);
                }

                // filter results according expressions
                foreach (var expr in borrowed ? Enumerable.Empty<BsonExpression>() : query.Filters)
                {
                    source = this.Filter(source, expr);
                }

                if (query.OrderBy != null)
                {
                    // pipe: orderby with offset+limit
                    source = this.OrderBy(source, query.OrderBy, query.Offset, query.Limit);
                }
                else
                {
                    // pipe: apply offset (no orderby)
                    if (!borrowed && !paginateNodes && query.Offset > 0) source = source.Skip(query.Offset);

                    // pipe: apply limit (no orderby)
                    if (!borrowed && !paginateNodes && query.Limit < int.MaxValue) source = source.Take(query.Limit);
                }
            }

            // do includes in result after filter
            foreach (var path in query.IncludeAfter)
            {
                source = this.Include(source, path);
            }

            if (query.VectorScore != null)
            {
                return query.VectorScore.Project(source, query.Select.Expression, query.Index as VectorIndexQuery, _pragmas.Collation);
            }

            // if is an aggregate query, run select transform over all resultset - will return a single value
            if (query.Select.All)
            {
                return this.SelectAll(source, query);
            }
            // run select transform in each document and return a new document or value
            else
            {
                return this.Select(source, query.Select.Expression);
            }
        }

        private IEnumerable<BsonDocument> AggregateNodes(IEnumerable<IndexNode> source,
            RowAggregate aggregate)
        {
            if (!aggregate.NeedsCount)
            {
                using (var enumerator = source.GetEnumerator())
                {
                    yield return aggregate.Project(enumerator.MoveNext() ? 1 : 0);
                }

                yield break;
            }

            var count = 0;

            foreach (var _ in source)
            {
                count = checked(count + 1);
                _transaction.Safepoint();
            }

            yield return aggregate.Project(count);
        }

        private IEnumerable<IndexNode> SkipNodes(IEnumerable<IndexNode> nodes, int offset)
        {
            foreach (var node in nodes)
            {
                if (offset > 0)
                {
                    offset--;
                    // Skipped index pages still count toward the transaction's
                    // memory budget. Do not retain a node across its safepoint.
                    _transaction.Safepoint();
                }
                else
                {
                    yield return node;
                }
            }
        }

        /// <summary>
        /// Pipe: Transaform final result appling expressin transform. Can return document or simple values
        /// </summary>
        private IEnumerable<BsonDocument> Select(IEnumerable<BsonDocument> source, BsonExpression select)
        {
            var defaultName = select.DefaultFieldName();

            foreach (var doc in source)
            {
                var value = select.ExecuteScalar(doc, _pragmas.Collation);

                if (value.IsDocument)
                {
                    yield return value.AsDocument;
                }
                else
                {
                    yield return new BsonDocument { [defaultName] = value, IsProjectionValue = true };
                }
            }
        }

        /// <summary>
        /// Pipe: Run select expression over all recordset
        /// </summary>
        private IEnumerable<BsonDocument> SelectAll(IEnumerable<BsonDocument> source, QueryPlan query)
        {
            var select = query.Select.Expression;
            using var cached = select.CanStreamAggregateSource ? null :
                new DocumentCacheEnumerable(source, _lookup, _transaction.Safepoint, drainOnDispose: false);

            // Aggregate expressions replay documents by address. Expand references again
            // on each enumeration because reloaded BSON contains the original DBRefs.
            if (cached != null)
            {
                source = cached;
                foreach (var path in query.IncludeBefore.Concat(query.IncludeAfter).Distinct())
                {
                    source = this.Include(source, path);
                }
            }

            var defaultName = select.DefaultFieldName();
            var result = select.Execute(source, _pragmas.Collation);

            foreach (var value in result)
            {
                if (value.IsDocument)
                {
                    yield return value.AsDocument;
                }
                else
                {
                    yield return new BsonDocument { [defaultName] = value, IsProjectionValue = true };
                }
            }
        }
    }
}
