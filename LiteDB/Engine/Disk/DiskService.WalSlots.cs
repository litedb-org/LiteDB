using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        // Protected by the WAL writer lock, except during single-threaded open.
        private readonly Dictionary<uint, long> _lastLogPositions = new Dictionary<uint, long>();
        private readonly SortedSet<long> _freeLogPositions = new SortedSet<long>();
        private uint _lastWalTransactionID;

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

        internal void RecordLogTransactionID(uint transactionID)
        {
            if (transactionID > _lastWalTransactionID)
            {
                _lastWalTransactionID = transactionID;
            }
        }

        private void RewriteLogTransactionIDs(Stream stream, IEnumerable<long> positions,
            uint transactionID)
        {
            var bytes = new byte[PAGE_SIZE];
            var buffer = new BufferSlice(bytes, 0, bytes.Length);

            foreach (var position in positions)
            {
                _cache.Invalidate(position, FileOrigin.Log);
                stream.Position = position;
                stream.ReadRequired(bytes, 0, bytes.Length);
                buffer.Write(transactionID, BasePage.P_TRANSACTION_ID);
                stream.Position = position;
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private long AllocateLogPosition(uint pageID, bool confirmation, bool transactionAnchored, long transactionMinimum)
        {
            // Appended confirmations define stable versions and order recovery.
            // Before any holes are reused, one frame for a transaction must reach
            // the physical tail. If allocations reached the writer out of order,
            // WriteLogDisk first advances the lower transaction ID and rewrites
            // its earlier frames. LiteDB 5.0.21 restores its counter from the last
            // physical frame rather than the maximum observed ID.
            // Storage that cannot sync (#2242) never reuses slots; see ReclaimLogPages.
            // A fresh engine (every shared-mode operation) has not yet learned that, so it
            // proves one log sync before reusing slots found at open.
            if (!confirmation && (ChecksumsEnabled || transactionAnchored) && !_logFlushDegraded &&
                _freeLogPositions.Count > 0 && this.ProveLogSync())
            {
                // v8 engines backfill in physical order. Preserve increasing
                // positions per page, even though commits use reclaimed capacity.
                var minimum = transactionMinimum;
                if (!ChecksumsEnabled && _lastLogPositions.TryGetValue(pageID, out var previous))
                    minimum = Math.Max(minimum, previous + PAGE_SIZE);
                using (var eligible = _freeLogPositions.GetViewBetween(minimum, long.MaxValue).GetEnumerator())
                {
                    if (eligible.MoveNext())
                    {
                        var position = eligible.Current;
                        _signals?.SlotReused();
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
                var empty = new byte[ChecksumsEnabled ? WalChecksum.FrameSize : PAGE_SIZE];
                var target = ChecksumsEnabled ? ((ChecksummedWalStream)stream).RawStream : stream;
                foreach (var position in positions)
                {
                    _cache.Invalidate(position, FileOrigin.Log);
                    if (ChecksumsEnabled && !Retirement.Slots.ContainsKey(position))
                        throw new PageChecksumException(FileOrigin.Log, position);
                    target.Position = ChecksumsEnabled ? position / PAGE_SIZE * WalChecksum.FrameSize : position;
                    target.Write(empty, 0, empty.Length);
                    this.CheckpointStage("wal-slot-cleared");
                }
                // Publish capacity only after clearing is durable. A failed/reused
                // write must never resurrect an old transaction's confirmation.
                SyncLogBarrier(stream);
                this.CheckpointStage("wal-slots-flushed");
                // Storage that cannot sync (#2242) keeps appending like dev: without a
                // durable clear, reusing a slot could overwrite a retired version that
                // unsynced data pages still depend on after a power loss.
                if (_logFlushDegraded) return;
                foreach (var position in positions) _freeLogPositions.Add(position);
                this.CheckpointStage("wal-slots-published");
            }
        }
    }
}
