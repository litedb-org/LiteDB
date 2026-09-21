using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Abstract class with workflow method to be used in pipeline implementation
    /// </summary>
    internal abstract partial class BasePipe
    {
        protected readonly TransactionService _transaction;
        protected readonly IDocumentLookup _lookup;
        protected readonly SortDisk _tempDisk;
        protected readonly EnginePragmas _pragmas;
        protected readonly uint _maxItemsCount;

        public BasePipe(TransactionService transaction, IDocumentLookup lookup, SortDisk tempDisk, EnginePragmas pragmas, uint maxItemsCount)
        {
            _transaction = transaction;
            _lookup = lookup;
            _tempDisk = tempDisk;
            _pragmas = pragmas;
            _maxItemsCount = maxItemsCount;
        }

        /// <summary>
        /// Abstract method to be implement according pipe workflow
        /// </summary>
        public abstract IEnumerable<BsonDocument> Pipe(IEnumerable<IndexNode> nodes, QueryPlan query);

        // load documents from document loader
        protected IEnumerable<BsonDocument> LoadDocument(IEnumerable<IndexNode> nodes)
        {
            foreach (var node in nodes)
            {
                yield return _lookup.Load(node);

                // check if transaction all full of pages to clear before continue
                _transaction.Safepoint();
            }
        }

        /// <summary>
        /// INCLUDE: Do include in result document according path expression - Works only with DocumentLookup
        /// </summary>
        protected IEnumerable<BsonDocument> Include(IEnumerable<BsonDocument> source, BsonExpression path)
        {
            // cached services
            string last = null;
            Snapshot snapshot = null;
            IndexService indexer = null;
            DataService data = null;
            CollectionIndex index = null;
            IDocumentLookup lookup = null;

            foreach (var doc in source)
            {
                foreach (var value in path.Execute(doc, _pragmas.Collation)
                                        .Where(x => x.IsDocument || x.IsArray)
                                        .ToList())
                {
                    // if value is document, convert this ref document into full document (do another query)
                    if (value.IsDocument)
                    {
                        DoInclude(value.AsDocument);
                    }
                    else
                    {
                        // if value is array, do same per item
                        foreach(var item in value.AsArray
                            .Where(x => x.IsDocument)
                            .Select(x => x.AsDocument))
                        {
                            DoInclude(item);
                        }
                    }

                }

                yield return doc;
            }

            void DoInclude(BsonDocument value)
            {
                // works only if is a document
                var refId = value["$id"];
                var refCol = value["$ref"];

                // if has no reference, just go out
                if (refId.IsNull || !refCol.IsString) return;

                // do some cache re-using when is same $ref (almost always is the same $ref collection)
                if (last != refCol.AsString)
                {
                    last = refCol.AsString;

                    // initialize services
                    snapshot = _transaction.CreateSnapshot(LockMode.Read, last, false);
                    indexer = new IndexService(snapshot, _pragmas.Collation, _maxItemsCount);
                    data = new DataService(snapshot, _maxItemsCount);

                    lookup = new DatafileLookup(data, _pragmas.UtcDate, null);

                    index = snapshot.CollectionPage?.PK;
                }

                // fill only if index and ref node exists
                if (index != null)
                {
                    var node = indexer.Find(index, refId, false, Query.Ascending);

                    if (node != null)
                    {
                        // load document based on dataBlock position
                        var refDoc = lookup.Load(node);

                        //do not remove $id
                        value.Remove("$ref");

                        // Keep $id for reference expressions and copy _id for ordinary
                        // entity mapping when the included value is projected on its own.
                        foreach (var element in refDoc.Where(x => !StringComparer.OrdinalIgnoreCase.Equals(x.Key, "$id")))
                        {
                            value[element.Key] = element.Value;
                        }

                        // Standalone projections also bypass the DbRef discriminator hook.
                        if (value.TryGetValue("$type", out var type)) value["_type"] = type;
                    }
                    else
                    {
                        // set in ref document that was not found
                        value["$missing"] = true;
                    }
                }

                _transaction.Safepoint();
            }
        }

        /// <summary>
        /// WHERE: Filter document according expression. Expression must be an Bool result
        /// </summary>
        protected IEnumerable<BsonDocument> Filter(IEnumerable<BsonDocument> source, BsonExpression expr)
        {
            foreach(var doc in source)
            {
                // checks if any result of expression is true
                var result = expr.ExecuteScalar(doc, _pragmas.Collation);

                if(result.IsBoolean && result.AsBoolean)
                {
                    yield return doc;
                }
            }
        }

        /// <summary>
        /// Evaluate all compatible residual filters while the BSON is still borrowed,
        /// yielding only addresses that may need owning materialization.
        /// </summary>
        protected IEnumerable<IndexNode> FilterBorrowed(IEnumerable<IndexNode> source,
            DatafileLookup lookup, BorrowedPredicateEvaluator predicate,
            IReadOnlyList<BsonExpression> fallbackFilters)
        {
            using var values = new BorrowedValueBuffer(predicate.SlotCount);
            var reader = lookup.CreateBorrowedReader(predicate);

            try
            {
                foreach (var node in source)
                {
                    BorrowedQueryDiagnostics.Examined();
                    BorrowedQueryDiagnostics.Executed();

                    var supported = lookup.TryEvaluate(node, reader, predicate, values,
                        _pragmas.Collation, out var matches);

                    if (!supported)
                    {
                        BorrowedQueryDiagnostics.FellBack();
                        matches = this.Matches(lookup.Load(node), fallbackFilters);
                    }

                    if (matches)
                    {
                        yield return node;
                    }

                    _transaction.Safepoint();
                }
            }
            finally
            {
                reader.Dispose();
            }
        }

        private bool Matches(BsonDocument document, IReadOnlyList<BsonExpression> filters)
        {
            for (var i = 0; i < filters.Count; i++)
            {
                var result = filters[i].ExecuteScalar(document, _pragmas.Collation);

                if (!result.IsBoolean || !result.AsBoolean) return false;
            }

            return true;
        }

        /// <summary>
        /// ORDER BY: Sort documents according orderby expression and order asc/desc
        /// </summary>
        protected IEnumerable<BsonDocument> OrderBy(IEnumerable<BsonDocument> source, OrderBy orderBy, int offset, int limit)
        {
            if (offset >= 0 && limit > 0 && (long)offset + limit <= TopNSort.MaximumCapacity)
            {
                foreach (var doc in this.OrderTopN(source, orderBy, offset, limit)) yield return doc;
                yield break;
            }
            var segments = orderBy.Segments;

            if (segments.Count == 1)
            {
                var segment = segments[0];
                var keyValues = source
                    .Select(doc => new KeyValuePair<BsonValue, PageAddress>(segment.ExecuteScalar(doc, _pragmas.Collation), doc.RawId));

                using (var sorter = new SortService(_tempDisk, new[] { segment.Order }, _pragmas))
                {
                    sorter.Insert(keyValues);

                    LOG($"sort {sorter.Count} keys in {sorter.Containers.Count} containers", "SORT");

                    var result = sorter.Sort().Skip(offset).Take(limit);

                    foreach (var keyValue in result)
                    {
                        var doc = _lookup.Load(keyValue.Value);

                        yield return doc;
                        _transaction.Safepoint();
                    }
                }
            }
            else
            {
                var orders = segments.Select(x => x.Order).ToArray();

                var keyValues = source
                    .Select(doc =>
                    {
                        var values = new BsonValue[segments.Count];

                        for (var i = 0; i < segments.Count; i++)
                        {
                            values[i] = segments[i].ExecuteScalar(doc, _pragmas.Collation);
                        }

                        return new KeyValuePair<BsonValue, PageAddress>(SortKey.FromValues(values, orders), doc.RawId);
                    });

                using (var sorter = new SortService(_tempDisk, orders, _pragmas))
                {
                    sorter.Insert(keyValues);

                    LOG($"sort {sorter.Count} keys in {sorter.Containers.Count} containers", "SORT");

                    var result = sorter.Sort().Skip(offset).Take(limit);

                    foreach (var keyValue in result)
                    {
                        var doc = _lookup.Load(keyValue.Value);

                        yield return doc;
                        _transaction.Safepoint();
                    }
                }
            }
        }

        /// <summary>
        /// Sort by scalar keys extracted from borrowed BSON, retaining only the
        /// owning key and address until the final window is known.
        /// </summary>
        protected IEnumerable<BsonDocument> OrderByBorrowed(IEnumerable<IndexNode> source,
            DatafileLookup lookup, OrderBy orderBy, BorrowedScalarEvaluator scalar,
            int offset, int limit)
        {
            var orders = orderBy.Segments.Select(x => x.Order).ToArray();
            var keyValues = this.ExtractBorrowedKeys(source, lookup, orderBy, scalar, orders);

            using (var sorter = new SortService(_tempDisk, orders, _pragmas))
            {
                sorter.Insert(keyValues);

                LOG($"sort {sorter.Count} borrowed keys in {sorter.Containers.Count} containers", "SORT");

                foreach (var keyValue in sorter.Sort().Skip(offset).Take(limit))
                {
                    yield return lookup.Load(keyValue.Value);
                    _transaction.Safepoint();
                }
            }
        }

        private IEnumerable<KeyValuePair<BsonValue, PageAddress>> ExtractBorrowedKeys(
            IEnumerable<IndexNode> source, DatafileLookup lookup, OrderBy orderBy,
            BorrowedScalarEvaluator scalar, int[] orders)
        {
            using var borrowed = new BorrowedValueBuffer(scalar.SlotCount);
            var reader = lookup.CreateBorrowedReader(scalar);

            try
            {
                foreach (var node in source)
                {
                    var supported = lookup.ReadBorrowed(node, reader, borrowed, scalar.SlotCount);
                    BsonValue key;

                    if (scalar.ValueCount == 1)
                    {
                        if (!supported || !scalar.TryGetValue(borrowed, 0, out key))
                        {
                            key = orderBy.Segments[0].ExecuteScalar(
                                lookup.Load(node), _pragmas.Collation);
                        }
                    }
                    else
                    {
                        var values = new BsonValue[scalar.ValueCount];

                        if (!supported || !scalar.TryGetValues(borrowed, values))
                        {
                            var document = lookup.Load(node);

                            for (var i = 0; i < values.Length; i++)
                            {
                                values[i] = orderBy.Segments[i].ExecuteScalar(
                                    document, _pragmas.Collation);
                            }
                        }

                        key = SortKey.FromValues(values, orders);
                    }

                    yield return new KeyValuePair<BsonValue, PageAddress>(key, node.DataBlock);
                    _transaction.Safepoint();
                }
            }
            finally
            {
                reader.Dispose();
            }
        }

        /// <summary>
        /// Build a direct-field result document from borrowed values without an
        /// intermediate owning source document.
        /// </summary>
        protected IEnumerable<BsonDocument> ProjectBorrowed(IEnumerable<IndexNode> source,
            DatafileLookup lookup, BorrowedProjectionEvaluator projection,
            BsonExpression select)
        {
            using var borrowed = new BorrowedValueBuffer(projection.SlotCount);
            var reader = lookup.CreateBorrowedReader(projection);
            var defaultName = select.DefaultFieldName();

            try
            {
                foreach (var node in source)
                {
                    var supported = lookup.ReadBorrowed(node, reader, borrowed, projection.SlotCount);

                    if (supported && projection.TryProject(borrowed, out var result))
                    {
                        yield return result;
                    }
                    else
                    {
                        var value = select.ExecuteScalar(lookup.Load(node), _pragmas.Collation);
                        yield return value.IsDocument
                            ? value.AsDocument
                            : new BsonDocument { [defaultName] = value, IsProjectionValue = true };
                    }

                    _transaction.Safepoint();
                }
            }
            finally
            {
                reader.Dispose();
            }
        }
    }
}
