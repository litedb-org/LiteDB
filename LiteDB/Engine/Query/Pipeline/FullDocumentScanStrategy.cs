namespace LiteDB.Engine
{
    /// <summary>
    /// A bounded, query-local probe for scans that cannot avoid owning documents.
    /// Only an entirely matching prefix selects owning filtering; a rejection
    /// keeps the borrowed prefilter for the rest of this enumeration.
    /// </summary>
    internal struct FullDocumentScanStrategy
    {
        internal const int ProbeLength = 32;
        private int _examined;
        private bool _rejected;

        internal bool UseOwning => _examined == ProbeLength && !_rejected;

        internal void Observe(bool matches)
        {
            if (_examined == ProbeLength) return;
            _examined++;
            _rejected |= !matches;
        }

        internal static bool CanUse(QueryPlan query) =>
            query.BorrowedFilter != null && query.Fields.Count == 0 &&
            !query.ForUpdate && query.GroupBy == null && !query.Select.All &&
            query.RowAggregate == null && query.VectorScore == null &&
            query.OrderBy == null && query.IncludeBefore.Count == 0 &&
            query.Offset == 0;
    }
}
