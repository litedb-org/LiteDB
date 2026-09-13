using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Represent a single snapshot
    /// </summary>
    internal class Snapshot : IDisposable
    {
        // instances from Engine
        private readonly HeaderPage _header;
        private readonly LockService _locker;
        private readonly DiskReader _reader;
        private readonly DiskService _disk;
        private readonly WalIndexService _walIndex;

        // instances from transaction
        private readonly uint _transactionID;
        private readonly TransactionPages _transPages;

        // snapshot controls
        private readonly int _readVersion;
        private readonly LockMode _mode;
        private readonly string _collectionName;
        private readonly CollectionPage _collectionPage;

        // local page cache - contains only pages about this collection (but do not contains CollectionPage - use this.CollectionPage)
        private readonly Dictionary<uint, BasePage> _localPages = new Dictionary<uint, BasePage>();

        private bool _disposed;

        // expose
        public LockMode Mode => _mode;
        public string CollectionName => _collectionName;
        public CollectionPage CollectionPage => _collectionPage;
        public ICollection<BasePage> LocalPages => _localPages.Values;
        public int ReadVersion => _readVersion;

        public Snapshot(
            LockMode mode, 
            string collectionName, 
            HeaderPage header, 
            uint transactionID, 
            TransactionPages transPages, 
            LockService locker, 
            WalIndexService walIndex, 
            DiskReader reader, 
            DiskService disk,
            bool addIfNotExists)
        {
            _mode = mode;
            _collectionName = collectionName;
            _header = header;
            _transactionID = transactionID;
            _transPages = transPages;
            _locker = locker;
            _walIndex = walIndex;
            _reader = reader;
            _disk = disk;

            // enter in lock mode according initial mode
            if (mode == LockMode.Write)
            {
                _locker.EnterLock(_collectionName);
            }

            // get lastest read version from wal-index
            _readVersion = _walIndex.CurrentReadVersion;

            var srv = new CollectionService(_header, _disk, this, _transPages);

            // read collection (create if new - load virtual too)
            srv.Get(_collectionName, addIfNotExists, ref _collectionPage);

            // clear local pages (will clear _collectionPage link reference)
            if (_collectionPage != null)
            {
                // local pages contains only data/index pages
                _localPages.Remove(_collectionPage.PageID);
            }
        }

        /// <summary>
        /// Get all snapshot pages (can or not include collectionPage) - If included, will be last page
        /// </summary>
        public IEnumerable<BasePage> GetWritablePages(bool dirty, bool includeCollectionPage)
        {
            ENSURE(!_disposed, "the snapshot is disposed");

            // if snapshot is read only, just exit
            if (_mode == LockMode.Read) yield break;

            foreach(var page in _localPages.Values.Where(x => x.IsDirty == dirty))
            {
                ENSURE(page.PageType != PageType.Header && page.PageType != PageType.Collection, "local cache cann't contains this page type");

                yield return page;
            }

            if (includeCollectionPage && _collectionPage != null && _collectionPage.IsDirty == dirty)
            {
                yield return _collectionPage;
            }
        }

        /// <summary>
        /// Clear all local pages and return page buffer to file reader. Do not release CollectionPage (only in Dispose method)
        /// </summary>
        public void Clear()
        {
            ENSURE(!_disposed, "the snapshot is disposed");

            // release pages only if snapshot are read only
            if (_mode == LockMode.Read)
            {
                // release all read pages (except collection page)
                foreach (var page in _localPages.Values)
                {
                    page.Buffer.Release();
                }
            }

            _localPages.Clear();
        }

        /// <summary>
        /// Dispose stream readers and exit collection lock
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            // release all data/index pages
            this.Clear();

            _disposed = true;

            // release collection page (in read mode)
            if (_mode == LockMode.Read && _collectionPage != null)
            {
                _collectionPage.Buffer.Release();
            }

            if(_mode == LockMode.Write)
            {
                _locker.ExitLock(_collectionName);
            }
        }

        #region Page Version functions

        /// <summary>
        /// Get a valid page for this snapshot (must consider local-index and wal-index)
        /// </summary>
        public T GetPage<T>(uint pageID, bool useLatestVersion = false)
            where T : BasePage
        {
            return this.GetPage<T>(pageID, out var origin, out var position, out var walVersion, useLatestVersion);
        }

        /// <summary>
        /// Get a valid page for this snapshot (must consider local-index and wal-index)
        /// </summary>
        public T GetPage<T>(uint pageID, out FileOrigin origin, out long position, out int walVersion, bool useLatestVersion = false)
            where T : BasePage
        {
            ENSURE(!_disposed, "the snapshot is disposed");
            ENSURE(pageID <= _header.LastPageID, "request page must be less or equals lastest page in data file");

            // check for header page (return header single instance)
            //TODO: remove this
            if (pageID == 0)
            {
                origin = FileOrigin.None;
                position = 0;
                walVersion = 0;

                return (T)(object)_header;
            }

            // look for this page inside local pages
            if (_localPages.TryGetValue(pageID, out var page))
            {
                origin = FileOrigin.None;
                position = 0;
                walVersion = 0;

                return (T)page;
            }

            // if page is not in local cache, get from disk (log/wal/data)
            page = this.ReadPage<T>(pageID, out origin, out position, out walVersion, useLatestVersion);

            // add into local pages
            _localPages[pageID] = page;

            // increment transaction size counter
            _transPages.TransactionSize++;

            return (T)page;
        }

        /// <summary>
        /// Read page from disk (dirty, wal or data)
        /// </summary>
        private T ReadPage<T>(uint pageID, out FileOrigin origin, out long position, out int walVersion, bool useLatestVersion = false)
            where T : BasePage
        {
            // if not inside local pages can be a dirty page saved in log file
            if (_transPages.DirtyPages.TryGetValue(pageID, out var walPosition))
            {
                // read page from log file
                var buffer = _reader.ReadPage(walPosition.Position, _mode == LockMode.Write, FileOrigin.Log);
                var dirty = BasePage.ReadPage<T>(buffer);

                origin = FileOrigin.Log;
                position = walPosition.Position;
                walVersion = _readVersion;

                ENSURE(dirty.TransactionID == _transactionID, "this page must came from same transaction");

                return dirty;
            }

            // now, look inside wal-index
            var pos = _walIndex.GetPageIndex(pageID, useLatestVersion ? int.MaxValue : _readVersion, out walVersion);

            if (pos != long.MaxValue)
            {
                // read page from log file
                var buffer = _reader.ReadPage(pos, _mode == LockMode.Write, FileOrigin.Log);
                var logPage = BasePage.ReadPage<T>(buffer);

                // clear some data inside this page (will be override when write on log file)
                logPage.TransactionID = 0;
                logPage.IsConfirmed = false;

                origin = FileOrigin.Log;
                position = pos;

                return logPage;
            }
            else
            {
                // for last chance, look inside original disk data file
                var pagePosition = BasePage.GetPagePosition(pageID);

                // read page from data file
                var buffer = _reader.ReadPage(pagePosition, _mode == LockMode.Write, FileOrigin.Data);
                var diskpage = BasePage.ReadPage<T>(buffer);

                origin = FileOrigin.Data;
                position = pagePosition;

                ENSURE(diskpage.IsConfirmed == false || diskpage.TransactionID != 0, "page are not header-clear in data file");

                return diskpage;
            }
        }

        /// <summary>
        /// Returns a page that contains space enough to data to insert new object - if one does not exit, creates a new page.
        /// Before return page, fix empty free list slot according with passed length
        /// </summary>
        public DataPage GetFreeDataPage(int bytesLength)
        {
            ENSURE(!_disposed, "the snapshot is disposed");

            var length = bytesLength + BasePage.SLOT_SIZE; // add +4 bytes for footer slot

            // get minimum slot to check for free page. Returns -1 if need NewPage
            var startSlot = DataPage.GetMinimumIndexSlot(length);

            // check for available re-usable page
            for (int currentSlot = startSlot; currentSlot >= 0; currentSlot--)
            {
                var freePageID = _collectionPage.FreeDataPageList[currentSlot];

                // there is no free page here, try find princess in another castle
                if (freePageID == uint.MaxValue) continue;

                var page = this.GetPage<DataPage>(freePageID);

                ENSURE(page.PageListSlot == currentSlot, "stored slot must be same as called");
                ENSURE(page.FreeBytes >= length, "ensure selected page has space enough for this content");

                // mark page page as dirty
                page.IsDirty = true;

                return page;
            }

            // if there is no re-usable page, create a new one
            return this.NewPage<DataPage>();
        }

        /// <summary>
        /// Get a index page with space enouth for a new index node
        /// </summary>
        public IndexPage GetFreeIndexPage(int bytesLength, ref uint freeIndexPageList)
        {
            ENSURE(!_disposed, "the snapshot is disposed");

            IndexPage page;

            // if there is not page in list pages, create new page
            if (freeIndexPageList == uint.MaxValue)
            {
                page = this.NewPage<IndexPage>();
            }
            else
            {
                // get first page of free list
                page = this.GetPage<IndexPage>(freeIndexPageList);

                ENSURE(page.FreeBytes > bytesLength, "this page shout be space enouth for this new node");
                ENSURE(page.PageListSlot == 0, "this page should be in slot #0");
            }

            return page;
        }

        /// <summary>
        /// Get a new empty page from disk: can be a reused page (from header free list) or file extend
        /// Never re-use page from same transaction
        /// </summary>
        public T NewPage<T>()
            where T : BasePage
        {
            ENSURE(!_disposed, "the snapshot is disposed");
            ENSURE(_collectionPage == null, typeof(T) == typeof(CollectionPage), "if no collection page defined yet, must be first request");
            ENSURE(typeof(T) == typeof(CollectionPage), _collectionPage == null, "there is no new collection page if page already exists");

            var pageID = 0u;
            PageBuffer buffer;

            // lock header instance to get new page
            lock (_header)
            {
                // there is need for _header.Savepoint() because changes here will incremental and will be persist later
                // if any problem occurs here, rollback will catch this changes

                // try get page from Empty free list
                if (_header.FreeEmptyPageList != uint.MaxValue)
                {
                    var free = this.GetPage<BasePage>(_header.FreeEmptyPageList, useLatestVersion: true);

                    ENSURE(free.PageType == PageType.Empty, "empty page must be defined as empty type");

                    // set header free empty page to next free page
                    _header.FreeEmptyPageList = free.NextPageID;

                    // clear NextPageID
                    free.NextPageID = uint.MaxValue;

                    // get pageID from empty list
                    pageID = free.PageID;

                    // get buffer inside re-used page
                    buffer = free.Buffer;
                }
                else
                {
                    // checks if not exceeded data file limit size
                    var newLength = (_header.LastPageID + 1) * PAGE_SIZE;

                    if (newLength > _header.Pragmas.LimitSize) throw new LiteException(0, $"Maximum data file size has been reached: {FileHelper.FormatFileSize(_header.Pragmas.LimitSize)}");

                    var savepoint = _header.Savepoint();
                    try
                    {
                        // increase LastPageID from shared page
                        pageID = ++_header.LastPageID;

                        // request for a new buffer
                        buffer = _reader.NewPage();
                    }
                    catch
                    {
                        // must revert all header content if any error occurs during header change
                        _header.Restore(savepoint);
                        throw;
                    }
                }

                // retain a list of created pages to, in a rollback situation, back pages to empty list
                _transPages.NewPages.Add(pageID);
            }

            var page = BasePage.CreatePage<T>(buffer, pageID);

            // update local cache with new instance T page type
            if (page.PageType != PageType.Collection)
            {
                _localPages[pageID] = page;
            }

            // define ColID for this new page (if this.CollectionPage is null, so this is new collection page)
            page.ColID = _collectionPage?.PageID ?? page.PageID;

            // define as dirty to override pageType
            page.IsDirty = true;

            // increment transaction size
            _transPages.TransactionSize++;

            return page;
        }

        /// <summary>
        /// Add/Remove a data page from free list slots
        /// </summary>
        public void AddOrRemoveFreeDataList(DataPage page)
        {
            ENSURE(!_disposed, "the snapshot is disposed");

            var newSlot = DataPage.FreeIndexSlot(page.FreeBytes);
            var initialSlot = page.PageListSlot;

            // there is no slot change - just exit (no need any change) [except if has no more items]
            if (newSlot == initialSlot && page.ItemsCount > 0) return;

            // remove from intial slot
            if (initialSlot != byte.MaxValue)
            {
                this.RemoveFreeList(page, ref _collectionPage.FreeDataPageList[initialSlot]);
            }

            // if there is no items, delete page
            if (page.ItemsCount == 0)
            {
                this.DeletePage(page);
            }
            else
            {
                // add into current slot
                this.AddFreeList(page, ref _collectionPage.FreeDataPageList[newSlot]);

                page.PageListSlot = newSlot;
            }
        }

        /// <summary>
        /// Add/Remove a index page from single free list
        /// </summary>
        public void AddOrRemoveFreeIndexList(IndexPage page, ref uint startPageID)
        {
            ENSURE(!_disposed, "the snapshot is disposed");

            var newSlot = IndexPage.FreeIndexSlot(page.FreeBytes);
            var isOnList = page.PageListSlot == 0;
            var mustKeep = newSlot == 0;

            // first, test if page should be deleted
            if (page.ItemsCount == 0)
            {
                if (isOnList)
                {
                    this.RemoveFreeList(page, ref startPageID);
                }

                this.DeletePage(page);
            }
            else
            {
                if (isOnList && !mustKeep)
                {
                    this.RemoveFreeList(page, ref startPageID);
                }
                else if (!isOnList && mustKeep)
                {
                    this.AddFreeList(page, ref startPageID);
                }

                page.PageListSlot = newSlot;
                page.IsDirty = true;

                // otherwise, nothing was changed
            }
        }

        /// <summary>
        /// Add page into double linked-list (always add as first element)
        /// </summary>
        private void AddFreeList<T>(T page, ref uint startPageID) where T : BasePage
        {
            ENSURE(page.PrevPageID == uint.MaxValue && page.NextPageID == uint.MaxValue, "only non-linked page can be added in linked list");

            // fix first/next page
            if (startPageID != uint.MaxValue)
            {
                var next = this.GetPage<T>(startPageID);
                next.PrevPageID = page.PageID;
                next.IsDirty = true;
            }

            page.PrevPageID = uint.MaxValue;
            page.NextPageID = startPageID;
            page.IsDirty = true;

            ENSURE(page.PageType == PageType.Data || page.PageType == PageType.Index, "only data/index pages must be first on free stack");

            startPageID = page.PageID;

            _collectionPage.IsDirty = true;
        }

        /// <summary>
        /// Remove a page from double linked list.
        /// </summary>
        private void RemoveFreeList<T>(T page, ref uint startPageID) where T : BasePage
        {
            // fix prev page
            if (page.PrevPageID != uint.MaxValue)
            {
                var prev = this.GetPage<T>(page.PrevPageID);
                prev.NextPageID = page.NextPageID;
                prev.IsDirty = true;
            }

            // fix next page
            if (page.NextPageID != uint.MaxValue)
            {
                var next = this.GetPage<T>(page.NextPageID);
                next.PrevPageID = page.PrevPageID;
                next.IsDirty = true;
            }

            // if page is first of the list set firstPage as next page
            if (startPageID == page.PageID)
            {
                startPageID = page.NextPageID;

                ENSURE(page.NextPageID == uint.MaxValue || this.GetPage<BasePage>(page.NextPageID).PageType != PageType.Empty, "first page on free stack must be non empty page");

                _collectionPage.IsDirty = true;
            }

            // clear page pointer (MaxValue = not used)
            page.PrevPageID = page.NextPageID = uint.MaxValue;
            page.IsDirty = true;
        }

        /// <summary>
        /// Delete a page - this page will be marked as Empty page
        /// There is no re-use deleted page in same transaction - deleted pages will be in another linked list and will
        /// be part of Header free list page only in commit
        /// </summary>
        private void DeletePage<T>(T page)
            where T : BasePage
        {
            ENSURE(page.PrevPageID == uint.MaxValue && page.NextPageID == uint.MaxValue, "before delete a page, no linked list with any another page");
            ENSURE(page.ItemsCount == 0 && page.UsedBytes == 0 && page.HighestIndex == byte.MaxValue && page.FragmentedBytes == 0, "no items on page when delete this page");
            ENSURE(page.PageType == PageType.Data || page.PageType == PageType.Index, "only data/index page can be deleted");
            DEBUG(!_collectionPage.FreeDataPageList.Any(x => x == page.PageID), "this page cann't be deleted because free data list page is linked o this page");
            DEBUG(!_collectionPage.GetCollectionIndexes().Any(x => x.FreeIndexPageList == page.PageID), "this page cann't be deleted because free index list page is linked o this page");
            DEBUG(page.Buffer.Slice(PAGE_HEADER_SIZE, PAGE_SIZE - PAGE_HEADER_SIZE - 1).All(0), "page content shloud be empty");

            // mark page as empty and dirty
            page.MarkAsEmtpy();

            // fix this page in free-link-list
            if (_transPages.FirstDeletedPageID == uint.MaxValue)
            {
                ENSURE(_transPages.DeletedPages == 0, "if has no firstDeletedPageID must has deleted pages");

                // set first and last deleted page as current deleted page
                _transPages.FirstDeletedPageID = page.PageID;
                _transPages.LastDeletedPageID = page.PageID;
            }
            else
            {
                ENSURE(_transPages.DeletedPages > 0, "must have at least 1 deleted page");

                // set next link from current deleted page to first deleted page
                page.NextPageID = _transPages.FirstDeletedPageID;

                // and then, set this current deleted page as first page making a linked list
                _transPages.FirstDeletedPageID = page.PageID;
            }

            _transPages.DeletedPages++;
        }

        #endregion

        #region DropCollection

        /// <summary>
        /// Delete current collection and all pages - this snapshot can't be used after this
        /// </summary>
        public void DropCollection(Action safePoint)
        {
            ENSURE(!_disposed, "the snapshot is disposed");

            var indexer = new IndexService(this, _header.Pragmas.Collation, _disk.MAX_ITEMS_COUNT);

            // CollectionPage will be last deleted page (there is no NextPageID from CollectionPage)
            _transPages.FirstDeletedPageID = _collectionPage.PageID;
            _transPages.LastDeletedPageID = _collectionPage.PageID;

            // mark collection page as empty
            _collectionPage.MarkAsEmtpy();

            _transPages.DeletedPages = 1;

            var indexPages = new HashSet<uint>();

            // getting all indexes pages from all indexes
            foreach(var index in _collectionPage.GetCollectionIndexes())
            {
                // add head/tail (same page) to be deleted
                indexPages.Add(index.Head.PageID);

                foreach (var node in indexer.FindAll(index, Query.Ascending))
                {
                    indexPages.Add(node.Page.PageID);

                    safePoint();
                }
            }

            // now, mark all pages as deleted
            foreach (var pageID in indexPages)
            {
                var page = this.GetPage<IndexPage>(pageID);

                // mark page as delete and fix deleted page list
                page.MarkAsEmtpy();

                page.NextPageID = _transPages.FirstDeletedPageID;
                _transPages.FirstDeletedPageID = page.PageID;

                _transPages.DeletedPages++;

                safePoint();
            }

            // adding all data pages
            foreach (var startPageID in _collectionPage.FreeDataPageList)
            {
                var next = startPageID;

                while(next != uint.MaxValue)
                {
                    var page = this.GetPage<DataPage>(next);

                    next = page.NextPageID;

                    // mark page as delete and fix deleted page list
                    page.MarkAsEmtpy();

                    page.NextPageID = _transPages.FirstDeletedPageID;
                    _transPages.FirstDeletedPageID = page.PageID;

                    _transPages.DeletedPages++;

                    safePoint();
                }
            }

            // remove collection name (in header) at commit time
            _transPages.Commit += (h) => h.DeleteCollection(_collectionName);
        }

        #endregion

        public override string ToString()
        {
            return $"{_collectionName} (pages: {_localPages.Count})";
        }
    }
}