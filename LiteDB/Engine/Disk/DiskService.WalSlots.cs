using System.Collections.Generic;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        // Protected by the WAL writer lock, except during single-threaded open.
        private readonly Dictionary<uint, long> _lastLogPositions = new Dictionary<uint, long>();
        private readonly SortedSet<long> _freeLogPositions = new SortedSet<long>();

        internal void RegisterFreeLogPosition(long position)
        {
            _freeLogPositions.Add(position);
        }

        internal void RecordLogPosition(uint pageID, long position)
        {
            if (!_lastLogPositions.TryGetValue(pageID, out var previous) || position > previous)
            {
                _lastLogPositions[pageID] = position;
            }
        }

        private long AllocateLogPosition(uint pageID, bool confirmation, bool transactionAnchored)
        {
            // Appended confirmations define stable versions and order recovery.
            // Before any holes are reused, one frame for a new transaction must
            // reach the physical tail. LiteDB 5.0.21 restores the transaction-ID
            // counter from the last physical frame rather than the maximum ID.
            // The tail anchor prevents an abandoned reused-slot transaction from
            // having its ID assigned to a later legacy transaction.
            if (!confirmation && transactionAnchored && _freeLogPositions.Count > 0)
            {
                // v8 engines backfill in physical order. Preserve increasing
                // positions per page, even though commits use reclaimed capacity.
                var minimum = _lastLogPositions.TryGetValue(pageID, out var previous) ? previous + PAGE_SIZE : 0;
                using (var eligible = _freeLogPositions.GetViewBetween(minimum, long.MaxValue).GetEnumerator())
                {
                    if (eligible.MoveNext())
                    {
                        var position = eligible.Current;
                        _freeLogPositions.Remove(position);
                        _cache.Invalidate(position, FileOrigin.Log);
                        return position;
                    }
                }
            }
            return Interlocked.Add(ref _logLength, PAGE_SIZE);
        }

        internal void ReclaimLogPages(IReadOnlyCollection<long> positions)
        {
            var stream = _writer.Value;
            this.CheckpointStage("before-wal-reclaim-lock");
            lock (stream)
            {
                var empty = new byte[PAGE_SIZE];
                foreach (var position in positions)
                {
                    _cache.Invalidate(position, FileOrigin.Log);
                    stream.Position = position;
                    stream.Write(empty, 0, empty.Length);
                    this.CheckpointStage("wal-slot-cleared");
                }
                // Publish capacity only after clearing is durable. A failed/reused
                // write must never resurrect an old transaction's confirmation.
                stream.FlushToDisk();
                this.CheckpointStage("wal-slots-flushed");
                foreach (var position in positions) _freeLogPositions.Add(position);
                this.CheckpointStage("wal-slots-published");
            }
        }
    }
}
