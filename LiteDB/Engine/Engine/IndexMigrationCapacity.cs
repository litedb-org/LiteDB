using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal sealed class IndexMigrationCapacity
    {
        private readonly long _limit;
        private readonly long _currentPages;
        private long _additionalPages;

        internal IndexMigrationCapacity(HeaderPage header, long? requestedLimit)
        {
            _limit = requestedLimit ?? header.Pragmas.LimitSize;
            _currentPages = (long)header.LastPageID + 1;
            if (_limit < header.Pragmas.LimitSize || _limit < _currentPages * PAGE_SIZE)
                throw new ArgumentException("IndexMigrationLimitSize must be at least the stored LIMIT_SIZE and database size.");
        }

        internal void AddOrdinaryIndex(long maximumNodeBytes, int retainedPages)
        {
            if (maximumNodeBytes == 0) return;
            // Every closed page has consumed at least this many bytes before the
            // free-list threshold rejects another node. Include slots in the input,
            // worst-case skip-list heights, and one partially filled final page.
            const int minimumUsed = PAGE_SIZE - PAGE_HEADER_SIZE - MAX_INDEX_LENGTH;
            maximumNodeBytes += 2 * (IndexNode.GetNodeLength(MAX_LEVEL_LENGTH, BsonValue.MinValue, out _) + BasePage.SLOT_SIZE);
            _additionalPages = checked(_additionalPages + Math.Max(0, maximumNodeBytes / minimumUsed + 1 - retainedPages));
        }

        internal void AddVectorDocument(int dimensions)
        {
            // At most one graph page plus external-vector data pages. Half-page
            // payloads and two spare pages conservatively cover BSON/block overhead.
            _additionalPages = checked(_additionalPages + 3 + (dimensions * 4L) / (PAGE_SIZE / 2));
        }

        internal void Validate(Snapshot snapshot, HeaderPage header)
        {
            var freePages = 0L;
            if (_limit != long.MaxValue && _additionalPages != 0)
            {
                var seen = new HashSet<uint>();
                var next = header.FreeEmptyPageList;
                // Match allocation's valid-prefix policy without changing a bad tail.
                while (next != uint.MaxValue && next <= header.LastPageID && seen.Add(next))
                {
                    var page = snapshot.GetPage<BasePage>(next);
                    if (page.PageType != PageType.Empty) break;
                    next = page.NextPageID;
                    freePages++;
                    snapshot.Safepoint();
                }
            }
            var required = checked((_currentPages + Math.Max(0, _additionalPages - freePages)) * PAGE_SIZE);
            if (required > _limit)
                throw new LiteException(0, "Index migration capacity exceeds LIMIT_SIZE. " +
                    "No migration changes were written. Retry with `index migration limit size=" + required +
                    "` (IndexMigrationLimitSize), or increase LIMIT_SIZE using the original engine. " +
                    "This conservative bound includes replacement pages and can exceed actual usage.");
        }
    }
}
