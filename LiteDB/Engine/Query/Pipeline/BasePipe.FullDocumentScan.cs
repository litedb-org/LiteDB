using System.Collections.Generic;

namespace LiteDB.Engine
{
    internal abstract partial class BasePipe
    {
        /// <summary>
        /// Probe with the borrowed predicate, then decode once and filter the
        /// owning document if the prefix indicates an all-match full scan.
        /// Rejected prefixes retain borrowed filtering. Both paths evaluate the
        /// same ordered residuals, before INCLUDE-after and pagination.
        /// </summary>
        protected IEnumerable<BsonDocument> FilterFullDocuments(IEnumerable<IndexNode> source,
            DatafileLookup lookup, BorrowedPredicateEvaluator predicate,
            IReadOnlyList<BsonExpression> filters)
        {
            var strategy = new FullDocumentScanStrategy();
            using var values = new BorrowedValueBuffer(predicate.SlotCount);
            using var reader = lookup.CreateBorrowedReader(predicate);

            foreach (var node in source)
            {
                BsonDocument document = null;
                bool matches;

                if (strategy.UseOwning)
                {
                    document = lookup.Load(node);
                    matches = this.Matches(document, filters);
                }
                else
                {
                    BorrowedQueryDiagnostics.Examined();
                    BorrowedQueryDiagnostics.Executed();
                    bool supported;

                    try
                    {
                        supported = lookup.TryEvaluate(node, reader, predicate, values,
                            _pragmas.Collation, out matches);
                    }
                    finally
                    {
                        // Drop page references before loading, yielding or a
                        // safepoint. The reader can be reset for the next row.
                        reader.Dispose();
                        values.Reset(predicate.SlotCount);
                    }

                    if (!supported)
                    {
                        BorrowedQueryDiagnostics.FellBack();
                        document = lookup.Load(node);
                        matches = this.Matches(document, filters);
                    }

                    strategy.Observe(matches);
                }

                if (matches)
                {
                    yield return document ?? lookup.Load(node);
                }

                _transaction.Safepoint();
            }
        }
    }
}
