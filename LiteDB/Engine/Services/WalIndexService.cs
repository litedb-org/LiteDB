using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Do all WAL index services based on LOG file - has only single instance per engine
    /// [Singleton - ThreadSafe]
    /// </summary>
    internal partial class WalIndexService
    {
        private const int READER_WAIT_MILLISECONDS = 10;
        private const int NO_WAIT_MILLISECONDS = 0;

        private readonly DiskService _disk;
        private readonly LockService _locker;
        private readonly CheckpointBackoff _backoff;
        // Shared engines close after each operation; their close checkpoints follow
        // the back-off that outlives the engine instead of running unconditionally.
        private readonly bool _rationClose;

        private readonly Dictionary<uint, List<KeyValuePair<int, long>>> _index = new Dictionary<uint, List<KeyValuePair<int, long>>>();
        private readonly ReaderWriterLockSlim _indexLock = new ReaderWriterLockSlim();

        private readonly HashSet<uint> _confirmTransactions = new HashSet<uint>();
        private readonly Func<object> _getCommitLock;
        private readonly ICoordinationSignals _signals;

        private int _currentReadVersion = 0;

        /// <summary>
        /// Store last used transaction ID
        /// </summary>
        private int _lastTransactionID = 0;

        public WalIndexService(DiskService disk, LockService locker, Func<int[]> sharedReaders = null, Func<object> getCommitLock = null,
            CheckpointBackoff backoff = null, ICoordinationSignals signals = null)
        {
            _disk = disk;
            _signals = signals;
            _locker = locker;
            _sharedReaders = sharedReaders;
            _backoff = backoff ?? new CheckpointBackoff();
            _rationClose = backoff != null;
            // Recovery and legacy migration can replace the header during open.
            // Resolve the same header monitor used by the resulting transactions.
            _getCommitLock = getCommitLock ?? (() => this);
        }

        /// <summary>
        /// Get current read version for all new transactions
        /// </summary>
        public int CurrentReadVersion
        {
            get
            {
                _indexLock.TryEnterReadLock(-1);

                try
                {
                    return _currentReadVersion;
                }
                finally
                {
                    _indexLock.ExitReadLock();
                }
            }
        }

        /// <summary>
        /// Get current counter for transaction ID
        /// </summary>
        public int LastTransactionID => _lastTransactionID;

        /// <summary>
        /// Clear WAL index links and cache memory. Used after checkpoint and rebuild rollback
        /// </summary>
        public void Clear()
        {
            _signals?.StructuralBegin();
            _indexLock.TryEnterWriteLock(-1);

            try
            {
                // reset 
                _confirmationPositions.Clear();
                _confirmTransactions.Clear();
                _index.Clear();

                _lastTransactionID = 0;
                _currentReadVersion = 0;
                _backfillVersion = 0;

                // clear cache
                _disk.ClearSchemaCache();
                _disk.Cache.Clear();

                // Invalidate the old generation only after checkpoint synced data.
                _disk.RotateWalSalt();
                // clear log file (sync)
                _disk.SetLength(0, FileOrigin.Log);
            }
            finally
            {
                _indexLock.ExitWriteLock();
                _signals?.StructuralEnd(0);
            }
        }

        /// <summary>
        /// Get new transactionID in thread safe way
        /// </summary>
        public uint NextTransactionID()
        {
            return (uint)Interlocked.Increment(ref _lastTransactionID);
        }

        /// <summary>
        /// Checks if a Page/Version are in WAL-index memory. Consider version that are below parameter. Returns PagePosition of this page inside WAL-file or Empty if page doesn't found.
        /// </summary>
        public long GetPageIndex(uint pageID, int version, out int walVersion)
        {
            // wal-index versions must be greater than 0 (version 0 is datafile)
            if (version == 0)
            {
                walVersion = 0;
                return long.MaxValue;
            }

            // to get page position, enter _index in read mode
            _indexLock.TryEnterReadLock(-1);

            try
            {
                // get page slot in cache
                if (_index.TryGetValue(pageID, out var list))
                {
                    // list are sorted by version number
                    var idx = list.Count;
                    var position = long.MaxValue;

                    walVersion = version;

                    // get all page versions in wal-index
                    // and then filter only equals-or-less then selected version
                    while (idx > 0)
                    {
                        idx--;

                        var v = list[idx];

                        if (v.Key <= version)
                        {
                            walVersion = v.Key;

                            position = v.Value;
                            break;
                        }
                    }

                    return position;
                }

                walVersion = int.MaxValue;

                return long.MaxValue;
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Add transactionID in confirmed list and update WAL index with all pages positions
        /// </summary>
        public void ConfirmTransaction(uint transactionID, ICollection<PagePosition> pagePositions, long headerPosition = long.MaxValue)
        {
            int visible;
            // must lock commit operation to update WAL-Index (memory only operation)
            _indexLock.TryEnterWriteLock(-1);

            try
            {
                // Confirmation frames always append. Their physical sequence is
                // stable even after obsolete transactions are removed from the WAL.
                var confirmation = headerPosition == long.MaxValue
                    ? pagePositions.Max(page => page.Position) : headerPosition;
                _currentReadVersion = checked((int)(confirmation / PAGE_SIZE + 1));
                _confirmationPositions[_currentReadVersion] = confirmation;
                _confirmTransactions.Add(transactionID);

                // update wal-index
                foreach (var pos in headerPosition == long.MaxValue ? pagePositions :
                    pagePositions.Concat(new[] { new PagePosition(0, headerPosition) }))
                {
                    if (_index.TryGetValue(pos.PageID, out var slot) == false)
                    {
                        slot = new List<KeyValuePair<int, long>>();

                        _index.Add(pos.PageID, slot);
                    }

                    // add version/position into pageID slot
                    slot.Add(new KeyValuePair<int, long>(_currentReadVersion, pos.Position));
                }
                visible = _currentReadVersion;
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
            _signals?.Committed(visible);
        }

        /// <summary>
        /// Load all confirmed transactions from log file (used only when open datafile)
        /// Don't need lock because it's called on ctor of LiteEngine
        /// </summary>
        public void RestoreIndex(ref HeaderPage header, Action<HeaderPage> validateHeader = null)
        {
            // get all page positions
            var positions = new Dictionary<long, List<PagePosition>>();


            var recovery = new WalRecovery();
            var pages = _disk.ReadFull(FileOrigin.Log);
            if (_disk.ChecksumsEnabled) pages = recovery.Read(pages);
            foreach (var buffer in pages)
            {
                var current = buffer.Position;
                if(buffer.IsBlank())
                {
                    // Durably cleared slots can be reused by later unconfirmed frames.
                    _disk.RegisterFreeLogPosition(current);
                    continue;
                }

                // read direct from buffer to avoid create BasePage structure
                var pageID = buffer.ReadUInt32(BasePage.P_PAGE_ID);
                if (!buffer.WalFrame.Retired) _disk.RecordLogPosition(pageID, current);
                var isConfirmed = buffer.ReadBool(BasePage.P_IS_CONFIRMED);
                var transactionID = buffer.ReadUInt32(BasePage.P_TRANSACTION_ID);
                _disk.RecordLogTransactionID(transactionID);

                var position = new PagePosition(pageID, current);

                if (!positions.TryGetValue(transactionID, out var list))
                    positions[transactionID] = list = new List<PagePosition>();
                if (!buffer.WalFrame.Retired) list.Add(position);

                if (isConfirmed)
                {
                    // Retired confirmation payloads are not live header/page images.
                    // Their witnesses still confirm the surviving frames at the same
                    // stable physical version as before reclamation.
                    var version = checked((int)(current / PAGE_SIZE + 1));
                    _confirmTransactions.Add(transactionID);
                    _currentReadVersion = version;
                    if (!buffer.WalFrame.Retired) _confirmationPositions[version] = current;
                    foreach (var entry in list)
                    {
                        if (!_index.TryGetValue(entry.PageID, out var versions))
                            _index[entry.PageID] = versions = new List<KeyValuePair<int, long>>();
                        versions.Add(new KeyValuePair<int, long>(version, entry.Position));
                    }
                    positions.Remove(transactionID);

                    var pageType = (PageType)buffer.ReadByte(BasePage.P_PAGE_TYPE);

                    // when a header is modified in transaction, must always be the last page inside log file (per transaction)
                    if (pageType == PageType.Header && !buffer.WalFrame.Retired) CopyConfirmedHeader(ref header, buffer);
                }

                // Keep the greatest observed ID, including abandoned transactions.
                // Reusing one would make its old pages appear committed.
                if (transactionID > unchecked((uint)_lastTransactionID))
                {
                    _lastTransactionID = unchecked((int)transactionID);
                }

            }
            if (_disk.ChecksumsEnabled)
            {
                validateHeader?.Invoke(header);
                _disk.FinishWalRecovery(recovery);
                this.RecordScan(recovery, positions.Values);
                var occupied = new HashSet<long>(_index.Values.SelectMany(x => x).Select(x => x.Value));
                foreach (var position in _disk.Retirement.Slots.Keys)
                    if (!occupied.Contains(position)) _disk.RegisterFreeLogPosition(position);
            }
        }


        private static void CopyConfirmedHeader(ref HeaderPage header, PageBuffer buffer)
        {
            // page buffer instance can't change
            var headerBuffer = header.Buffer;
            var fileVersion = header.FileVersion;

            // copy this buffer block into original header block
            Buffer.BlockCopy(buffer.Array, buffer.Offset, headerBuffer.Array, headerBuffer.Offset, PAGE_SIZE);

            // re-load header (using new buffer data)
            header = new HeaderPage(headerBuffer);
            header.EnsureVersion(fileVersion);
            header.TransactionID = uint.MaxValue;
            header.IsConfirmed = false;
        }
    }
}
