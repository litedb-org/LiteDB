using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Experimental coordinator: a read-only snapshot engine can advance to transactions
    /// appended after it opened, instead of replaying the whole WAL again.
    /// </summary>
    internal partial class WalIndexService
    {
        // Where the last scan left off: its confirmation sequence and end, and the first
        // position that may still hold a frame of a transaction it saw unconfirmed or
        // did not reach. Frames before it belong to transactions already applied.
        private long _scanSequence;
        private long _scanConfirmedEnd;
        private long _rescanFrom;

        private void RecordScan(WalRecovery recovery, IEnumerable<List<PagePosition>> unconfirmed)
        {
            _scanSequence = recovery.Sequence;
            _scanConfirmedEnd = recovery.ConfirmedEnd;
            _rescanFrom = recovery.ConfirmedEnd;
            foreach (var frames in unconfirmed)
                foreach (var frame in frames)
                    _rescanFrom = Math.Min(_rescanFrom, frame.Position);
        }

        /// <summary>
        /// Apply the transactions confirmed from the rescan position to the WAL's current
        /// end, and return the new read version. Correct only if no frame before that
        /// position changed since the last scan: no checkpoint, WAL reset or reuse of a
        /// reclaimed slot. The coordinator's status page proves that to the caller, and
        /// no query may run on this engine meanwhile.
        /// </summary>
        internal int ExtendIndex(HeaderPage header)
        {
            if (!_disk.ChecksumsEnabled) return _currentReadVersion;
            _indexLock.EnterWriteLock();
            try
            {
                var recovery = new WalRecovery(_scanSequence, _scanConfirmedEnd);
                var positions = new Dictionary<long, List<PagePosition>>();
                // Frames of transactions applied earlier (including retired witnesses)
                // precede the ones still open; their confirmations are not repeated.
                var frames = _disk.ReadLogFrom(_rescanFrom).Where(page => !page.IsBlank() && !page.WalFrame.Retired &&
                    !_confirmTransactions.Contains(page.ReadUInt32(BasePage.P_TRANSACTION_ID)));
                foreach (var buffer in recovery.Read(frames))
                {
                    var pageID = buffer.ReadUInt32(BasePage.P_PAGE_ID);
                    var transactionID = buffer.ReadUInt32(BasePage.P_TRANSACTION_ID);
                    if (!positions.TryGetValue(transactionID, out var list))
                        positions[transactionID] = list = new List<PagePosition>();
                    list.Add(new PagePosition(pageID, buffer.Position));
                    if (!buffer.ReadBool(BasePage.P_IS_CONFIRMED)) continue;

                    var version = checked((int)(buffer.Position / PAGE_SIZE + 1));
                    _confirmTransactions.Add(transactionID);
                    _currentReadVersion = version;
                    _confirmationPositions[version] = buffer.Position;
                    foreach (var entry in list)
                    {
                        if (!_index.TryGetValue(entry.PageID, out var versions))
                            _index[entry.PageID] = versions = new List<KeyValuePair<int, long>>();
                        versions.Add(new KeyValuePair<int, long>(version, entry.Position));
                    }
                    positions.Remove(transactionID);
                    if ((PageType)buffer.ReadByte(BasePage.P_PAGE_TYPE) == PageType.Header)
                    {
                        // In place: the transaction monitor and lock service hold this
                        // instance (at open, restore runs before they exist and may replace it).
                        header.Restore(buffer);
                        header.TransactionID = uint.MaxValue;
                        header.IsConfirmed = false;
                    }
                }
                this.RecordScan(recovery, positions.Values);
                return _currentReadVersion;
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }

#if DEBUG || TESTING
        /// <summary>Deterministic description of the index, for comparing two engines.</summary>
        internal string DescribeIndex()
        {
            _indexLock.EnterReadLock();
            try
            {
                var text = new StringBuilder();
                text.Append("v=").Append(_currentReadVersion).Append(";t=").Append(string.Join(",", _confirmTransactions.OrderBy(x => x)));
                foreach (var entry in _index.OrderBy(x => x.Key))
                    text.Append(';').Append(entry.Key).Append(':')
                        .Append(string.Join(",", entry.Value.Select(x => x.Key + "@" + x.Value)));
                return text.ToString();
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }
#endif
    }
}
