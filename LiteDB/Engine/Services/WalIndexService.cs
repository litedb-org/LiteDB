using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Do all WAL index services based on LOG file - has only single instance per engine
    /// [Singleton - ThreadSafe]
    /// </summary>
    internal class WalIndexService
    {
        private readonly DiskService _disk;
        private readonly LockService _locker;

        private readonly Dictionary<uint, List<KeyValuePair<int, long>>> _index = new Dictionary<uint, List<KeyValuePair<int, long>>>();
        private readonly ReaderWriterLockSlim _indexLock = new ReaderWriterLockSlim();

        private readonly HashSet<uint> _confirmTransactions = new HashSet<uint>();

        private int _currentReadVersion = 0;

        /// <summary>
        /// Store last used transaction ID
        /// </summary>
        private int _lastTransactionID = 0;

        public WalIndexService(DiskService disk, LockService locker)
        {
            _disk = disk;
            _locker = locker;
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
            _indexLock.TryEnterWriteLock(-1);

            try
            {
                // reset 
                _confirmTransactions.Clear();
                _index.Clear();

                _lastTransactionID = 0;
                _currentReadVersion = 0;

                // clear cache
                _disk.Cache.Clear();

                // clear log file (sync)
                _disk.SetLength(0, FileOrigin.Log);
            }
            finally
            {
                _indexLock.ExitWriteLock();
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
        public void ConfirmTransaction(uint transactionID, ICollection<PagePosition> pagePositions)
        {
            // must lock commit operation to update WAL-Index (memory only operation)
            _indexLock.TryEnterWriteLock(-1);

            try
            {
                // increment current version
                _currentReadVersion++;

                // update wal-index
                foreach (var pos in pagePositions)
                {
                    if (_index.TryGetValue(pos.PageID, out var slot) == false)
                    {
                        slot = new List<KeyValuePair<int, long>>();

                        _index.Add(pos.PageID, slot);
                    }

                    // add version/position into pageID slot
                    slot.Add(new KeyValuePair<int, long>(_currentReadVersion, pos.Position));
                }

                // add transaction as confirmed
                _confirmTransactions.Add(transactionID);
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Load all confirmed transactions from log file (used only when open datafile)
        /// Don't need lock because it's called on ctor of LiteEngine
        /// </summary>
        public void RestoreIndex(ref HeaderPage header)
        {
            // get all page positions
            var positions = new Dictionary<long, List<PagePosition>>();
            var current = 0L;

            // read all pages to get confirmed transactions (do not read page content, only page header)
            foreach (var buffer in _disk.ReadFull(FileOrigin.Log))
            {
                if(buffer.IsBlank())
                {
                    // this should not happen, but if it does, it means there's a zeroed page in the file
                    // just skip it
                    current += PAGE_SIZE;
                    continue;
                }

                // read direct from buffer to avoid create BasePage structure
                var pageID = buffer.ReadUInt32(BasePage.P_PAGE_ID);
                var isConfirmed = buffer.ReadBool(BasePage.P_IS_CONFIRMED);
                var transactionID = buffer.ReadUInt32(BasePage.P_TRANSACTION_ID);

                var position = new PagePosition(pageID, current);

                if (positions.TryGetValue(transactionID, out var list))
                {
                    list.Add(position);
                }
                else
                {
                    positions[transactionID] = new List<PagePosition> { position };
                }

                if (isConfirmed)
                {
                    this.ConfirmTransaction(transactionID, positions[transactionID]);

                    var pageType = (PageType)buffer.ReadByte(BasePage.P_PAGE_TYPE);

                    // when a header is modified in transaction, must always be the last page inside log file (per transaction)
                    if (pageType == PageType.Header)
                    {
                        // page buffer instance can't change
                        var headerBuffer = header.Buffer;

                        // copy this buffer block into original header block
                        Buffer.BlockCopy(buffer.Array, buffer.Offset, headerBuffer.Array, headerBuffer.Offset, PAGE_SIZE);

                        // re-load header (using new buffer data)
                        header = new HeaderPage(headerBuffer);
                        header.TransactionID = uint.MaxValue;
                        header.IsConfirmed = false;
                    }
                }

                // update last transaction ID
                _lastTransactionID = (int)transactionID;

                current += PAGE_SIZE;
            }
        }

        /// <summary>
        /// Do checkpoint operation to copy log pages into data file. Return how many transactions was commited inside data file
        /// Checkpoint requires exclusive lock database
        /// </summary>
        public int Checkpoint()
        {
            // no log file or no confirmed transaction, just exit
            if (_disk.GetFileLength(FileOrigin.Log) == 0 || _confirmTransactions.Count == 0) return 0;

            var mustExit = _locker.EnterExclusive();

            try
            {
                return this.CheckpointInternal();
            }
            finally
            {
                if (mustExit)
                {
                    _locker.ExitExclusive();
                }
            }
        }

        /// <summary>
        /// Run checkpoint only if there is no open transactions
        /// </summary>
        public int TryCheckpoint()
        {
            // no log file or no confirmed transaction, just exit
            if (_disk.GetFileLength(FileOrigin.Log) == 0 || _confirmTransactions.Count == 0) return 0;

            if (_locker.TryEnterExclusive(out var mustExit) == false) return 0;

            try
            {
                return this.CheckpointInternal();
            }
            finally
            {
                if (mustExit)
                {
                    _locker.ExitExclusive();
                }
            }
        }

        /// <summary>
        /// Do checkpoint operation to copy log pages into data file. Return how many transactions was commited inside data file
        /// Checkpoint requires exclusive lock database
        /// If soft = true, just try enter in exclusive mode - if not possible, just exit (don't execute checkpoint)
        /// </summary>
        private int CheckpointInternal()
        {
            LOG($"checkpoint", "WAL");

            var counter = 0;

            // getting all "good" pages from log file to be copied into data file
            IEnumerable<PageBuffer> source()
            {
                foreach (var buffer in _disk.ReadFull(FileOrigin.Log))
                {
                    if (buffer.IsBlank())
                    {
                        // this should not happen, but if it does, it means there's a zeroed page in the file
                        // just skip it
                        continue;
                    }

                    // read direct from buffer to avoid create BasePage structure
                    var transactionID = buffer.ReadUInt32(BasePage.P_TRANSACTION_ID);

                    // only confied paged can be write on data disk
                    if (_confirmTransactions.Contains(transactionID))
                    {
                        var pageID = buffer.ReadUInt32(BasePage.P_PAGE_ID);

                        // clear isConfirmed/transactionID
                        buffer.Write(uint.MaxValue, BasePage.P_TRANSACTION_ID);
                        buffer.Write(false, BasePage.P_IS_CONFIRMED);

                        buffer.Position = BasePage.GetPagePosition(pageID);

                        counter++;

                        yield return buffer;
                    }
                }
            }

            // write all log pages into data file (sync)
            _disk.WriteDataDisk(source());

            // clear log file, clear wal index, memory cache,
            this.Clear();

            return counter;
        }
    }
}