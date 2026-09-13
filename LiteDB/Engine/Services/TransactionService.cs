using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Represent a single transaction service. Need a new instance for each transaction.
    /// You must run each transaction in a different thread - no 2 transaction in same thread (locks as per-thread)
    /// </summary>
    internal class TransactionService : IDisposable
    {
        // instances from Engine
        private readonly HeaderPage _header;
        private readonly LockService _locker;
        private readonly DiskService _disk;
        private readonly DiskReader _reader;
        private readonly WalIndexService _walIndex;
        private readonly TransactionMonitor _monitor;

        // transaction controls
        private readonly Dictionary<string, Snapshot> _snapshots = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly TransactionPages _transPages = new TransactionPages();

        // transaction info
        private readonly int _threadID = Environment.CurrentManagedThreadId;
        private readonly uint _transactionID;
        private readonly DateTime _startTime;
        private LockMode _mode = LockMode.Read;
        private TransactionState _state = TransactionState.Active;

        // expose (as read only)
        public int ThreadID => _threadID;
        public uint TransactionID => _transactionID;
        public TransactionState State => _state;
        public LockMode Mode => _mode;
        public TransactionPages Pages => _transPages;
        public DateTime StartTime => _startTime;
        public IEnumerable<Snapshot> Snapshots => _snapshots.Values;
        public bool QueryOnly { get; }

        // get/set
        public int MaxTransactionSize { get; set; }

        /// <summary>
        /// Get/Set how many open cursor this transaction are running
        /// </summary>
        public List<CursorInfo> OpenCursors { get; } = new List<CursorInfo>();

        /// <summary>
        /// Get/Set if this transaction was opened by BeginTrans() method (not by AutoTransaction/Cursor)
        /// </summary>
        public bool ExplicitTransaction { get; set; } = false;

        public TransactionService(HeaderPage header, LockService locker, DiskService disk, WalIndexService walIndex, int maxTransactionSize, TransactionMonitor monitor, bool queryOnly)
        {
            // retain instances
            _header = header;
            _locker = locker;
            _disk = disk;
            _walIndex = walIndex;
            _monitor = monitor;

            this.QueryOnly = queryOnly;
            this.MaxTransactionSize = maxTransactionSize;

            // create new transactionID
            _transactionID = walIndex.NextTransactionID();
            _startTime = DateTime.UtcNow;
            _reader = _disk.GetReader();
        }

        /// <summary>
        /// Finalizer: Will be called once a thread is closed. The TransactionMonitor._slot releases the used TransactionService.
        /// </summary>
        ~TransactionService()
        {
            Dispose(false);
        }

        /// <summary>
        /// Create (or get from transaction-cache) snapshot and return
        /// </summary>
        public Snapshot CreateSnapshot(LockMode mode, string collection, bool addIfNotExists)
        {
            ENSURE(_state == TransactionState.Active, "transaction must be active to create new snapshot");

            Snapshot create() => new Snapshot(mode, collection, _header, _transactionID, _transPages, _locker, _walIndex, _reader, _disk, addIfNotExists);

            if (_snapshots.TryGetValue(collection, out var snapshot))
            {
                // if current snapshot are ReadOnly but request is about Write mode, dispose read and re-create new in WriteMode
                // or if a previous snapshot was opened with addIfNotExists=false
                if ((mode == LockMode.Write && snapshot.Mode == LockMode.Read) || (addIfNotExists && snapshot.CollectionPage == null))
                {
                    // dispose current read-only snapshot
                    snapshot.Dispose();

                    // must remove before try add again - create() method can throw lock exception
                    _snapshots.Remove(collection);

                    // create new snapshot with write mode
                    _snapshots[collection] = snapshot = create();
                }
            }
            else
            {
                // if not exits, let's create here
                _snapshots[collection] = snapshot = create();
            }

            // update transaction mode to write in first write snaphost request
            if (mode == LockMode.Write) _mode = LockMode.Write;

            return snapshot;
        }

        /// <summary>
        /// If current transaction contains too much pages, now is safe to remove clean pages from memory and flush to wal disk dirty pages
        /// </summary>
        public void Safepoint()
        {
            if (_state != TransactionState.Active) throw new LiteException(0, "This transaction are invalid state");

            if (_monitor.CheckSafepoint(this))
            {
                LOG($"safepoint flushing transaction pages: {_transPages.TransactionSize}", "TRANSACTION");

                // if any snapshot are writable, persist pages
                if (_mode == LockMode.Write)
                {
                    this.PersistDirtyPages(false);
                }

                // clear local pages in all snapshots (read/write snapshosts)
                foreach (var snapshot in _snapshots.Values)
                {
                    snapshot.Clear();
                }

                // there is no local pages in cache and all dirty pages are in log file
                _transPages.TransactionSize = 0;
            }
        }

        /// <summary>
        /// Persist all dirty in-memory pages (in all snapshots) and clear local pages list (even clean pages)
        /// </summary>
        private int PersistDirtyPages(bool commit)
        {
            var dirty = 0;

            // inner method to get all dirty pages
            IEnumerable<PageBuffer> source()
            {
                // get all dirty pages from all write snapshots
                // can include (or not) collection pages
                // update DirtyPagesLog inside transPage for all dirty pages was write on disk
                var pages = _snapshots.Values
                    .Where(x => x.Mode == LockMode.Write)
                    .SelectMany(x => x.GetWritablePages(true, commit));

                // mark last dirty page as confirmed only if there is no header change in commit
                var markLastAsConfirmed = commit && _transPages.HeaderChanged == false;

                // neet use "IsLast" method to get when loop are last item
                foreach (var page in pages.IsLast())
                {
                    // update page transactionID
                    page.Item.TransactionID = _transactionID;

                    // if last page, mask as confirm (only if a real commit and no header changes)
                    if (page.IsLast)
                    {
                        page.Item.IsConfirmed = markLastAsConfirmed;
                    }

                    // if current page is last deleted page, point this page to last free
                    if (_transPages.LastDeletedPageID == page.Item.PageID && commit)
                    {
                        ENSURE(_transPages.HeaderChanged, "must header be in lock");
                        ENSURE(page.Item.PageType == PageType.Empty, "must be marked as deleted page");

                        // join existing free list pages into new list of deleted pages
                        page.Item.NextPageID = _header.FreeEmptyPageList;

                        // and now, set header free list page to this new list
                        _header.FreeEmptyPageList = _transPages.FirstDeletedPageID;
                    }

                    var buffer = page.Item.UpdateBuffer();

                    // buffer position will be set at end of file (it´s always log file)
                    yield return buffer;

                    dirty++;

                    _transPages.DirtyPages[page.Item.PageID] = new PagePosition(page.Item.PageID, buffer.Position);
                }

                // in commit with header page change, last page will be header
                if (commit && _transPages.HeaderChanged)
                {
                    // update this confirm page with current transactionID
                    _header.TransactionID = _transactionID;

                    // this header page will be marked as confirmed page in log file
                    _header.IsConfirmed = true;

                    // invoke all header callbacks (new/drop collections)
                    _transPages.OnCommit(_header);

                    // clone header page
                    var buffer = _header.UpdateBuffer();
                    var clone = _disk.NewPage();

                    // mem copy from current header to new header clone
                    Buffer.BlockCopy(buffer.Array, buffer.Offset, clone.Array, clone.Offset, clone.Count);

                    // persist header in log file
                    yield return clone;
                }
            };

            // write all dirty pages, in sequence on log-file and store references into log pages on transPages
            // (works only for Write snapshots)
            var count = _disk.WriteLogDisk(source());

            // now, discard all clean pages (because those pages are writable and must be readable)
            // from write snapshots
            _disk.DiscardCleanPages(_snapshots.Values
                    .Where(x => x.Mode == LockMode.Write)
                    .SelectMany(x => x.GetWritablePages(false, commit))
                    .Select(x => x.Buffer));

            return count;
        }

        /// <summary>
        /// Write pages into disk and confirm transaction in wal-index. Returns true if any dirty page was updated
        /// After commit, all snapshot are closed
        /// </summary>
        public void Commit()
        {
            ENSURE(_state == TransactionState.Active, "transaction must be active to commit (current state: {0})", _state);

            LOG($"commit transaction ({_transPages.TransactionSize} pages)", "TRANSACTION");

            if (_mode == LockMode.Write || _transPages.HeaderChanged)
            {
                lock (_header)
                {
                    // persist all dirty page as commit mode (mark last page as IsConfirm)
                    var count = this.PersistDirtyPages(true);

                    // update wal-index (if any page was added into log disk)
                    if (count > 0)
                    {
                        _walIndex.ConfirmTransaction(_transactionID, _transPages.DirtyPages.Values);
                    }
                }
            }

            // dispose all snapshots
            foreach (var snapshot in _snapshots.Values)
            {
                snapshot.Dispose();
            }

            _state = TransactionState.Committed;
        }

        /// <summary>
        /// Rollback transaction operation - ignore all modified pages and return new pages into disk
        /// After rollback, all snapshot are closed
        /// </summary>
        public void Rollback()
        {
            ENSURE(_state == TransactionState.Active, "transaction must be active to rollback (current state: {0})", _state);

            LOG($"rollback transaction ({_transPages.TransactionSize} pages with {_transPages.NewPages.Count} returns)", "TRANSACTION");

            // if transaction contains new pages, must return to database in another transaction
            if (_transPages.NewPages.Count > 0)
            {
                this.ReturnNewPages();
            }

            // dispose all snapshots
            foreach (var snapshot in _snapshots.Values)
            {
                // but first, if writable, discard changes
                if (snapshot.Mode == LockMode.Write)
                {
                    // discard all dirty pages
                    _disk.DiscardDirtyPages(snapshot.GetWritablePages(true, true).Select(x => x.Buffer));

                    // discard all clean pages
                    _disk.DiscardCleanPages(snapshot.GetWritablePages(false, true).Select(x => x.Buffer));
                }

                // now, release pages
                snapshot.Dispose();
            }

            _state = TransactionState.Aborted;
        }

        /// <summary>
        /// Return added pages when occurs an rollback transaction (run this only in rollback). Create new transactionID and add into
        /// Log file all new pages as EmptyPage in a linked order - also, update SharedPage before store
        /// </summary>
        private void ReturnNewPages()
        {
            // create new transaction ID
            var transactionID = _walIndex.NextTransactionID();

            // now lock header to update LastTransactionID/FreePageList
            lock (_header)
            {
                // persist all empty pages into wal-file
                var pagePositions = new Dictionary<uint, PagePosition>();

                IEnumerable<PageBuffer> source()
                {
                    // create list of empty pages with forward link pointer
                    for (var i = 0; i < _transPages.NewPages.Count; i++)
                    {
                        var pageID = _transPages.NewPages[i];
                        var next = i < _transPages.NewPages.Count - 1 ? _transPages.NewPages[i + 1] : _header.FreeEmptyPageList;

                        var buffer = _disk.NewPage();

                        var page = new BasePage(buffer, pageID, PageType.Empty)
                        {
                            NextPageID = next,
                            TransactionID = transactionID
                        };

                        yield return page.UpdateBuffer();

                        // update wal
                        pagePositions[pageID] = new PagePosition(pageID, buffer.Position);
                    }

                    // update header page with my new transaction ID
                    _header.TransactionID = transactionID;
                    _header.FreeEmptyPageList = _transPages.NewPages[0];
                    _header.IsConfirmed = true;

                    // clone header buffer
                    var buf = _header.UpdateBuffer();
                    var clone = _disk.NewPage();

                    Buffer.BlockCopy(buf.Array, buf.Offset, clone.Array, clone.Offset, clone.Count);

                    yield return clone;
                };

                // create a header save point before any change
                var safepoint = _header.Savepoint();

                try
                {
                    // write all pages (including new header)
                    _disk.WriteLogDisk(source());
                }
                catch
                {
                    // must revert all header content if any error occurs during header change
                    _header.Restore(safepoint);
                    throw;
                }

                // now confirm this transaction to wal
                _walIndex.ConfirmTransaction(transactionID, pagePositions.Values);
            }
        }

        /// <summary>
        /// Public implementation of Dispose pattern.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        // Protected implementation of Dispose pattern.
        protected virtual void Dispose(bool dispose)
        {
            if (_state == TransactionState.Disposed)
            {
                return;
            }

            ENSURE(_state != TransactionState.Disposed, "transaction must be active before call Done");

            // clean snapshots if there is no commit/rollback
            if (_state == TransactionState.Active && _snapshots.Count > 0)
            {
                // release writable snapshots
                foreach (var snapshot in _snapshots.Values.Where(x => x.Mode == LockMode.Write))
                {
                    // discard all dirty pages
                    _disk.DiscardDirtyPages(snapshot.GetWritablePages(true, true).Select(x => x.Buffer));

                    // discard all clean pages
                    _disk.DiscardCleanPages(snapshot.GetWritablePages(false, true).Select(x => x.Buffer));
                }

                // release buffers in read-only snaphosts
                foreach (var snapshot in _snapshots.Values.Where(x => x.Mode == LockMode.Read))
                {
                    foreach (var page in snapshot.LocalPages)
                    {
                        page.Buffer.Release();
                    }

                    snapshot.CollectionPage?.Buffer.Release();
                }
            }

            _reader.Dispose();

            _state = TransactionState.Disposed;

            if (!dispose)
            {
                // Remove transaction monitor's dictionary
                _monitor.ReleaseTransaction(this);
            }
        }
    }
}