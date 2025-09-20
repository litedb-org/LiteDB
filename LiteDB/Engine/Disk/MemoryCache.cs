using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Manage linear memory segments to avoid re-creating array buffer in heap memory
    /// Do not share same memory store with different files
    /// [ThreadSafe]
    /// </summary>
    internal class MemoryCache : IDisposable
    {
        /// <summary>
        /// Contains free ready-to-use pages in memory
        /// - All pages here MUST have ShareCounter = 0
        /// - All pages here MUST have Position = MaxValue
        /// </summary>
        private readonly ConcurrentQueue<PageBuffer> _free = new ConcurrentQueue<PageBuffer>();

        /// <summary>
        /// Contains only clean pages (from both data/log file) - support page concurrency use
        /// - MUST have defined Origin and Position
        /// - Contains only 1 instance per Position/Origin
        /// - Contains only pages with ShareCounter >= 0
        /// *  = 0 - Page is available but is not in use by anyone (can be moved into _free list on next Extend())
        /// * >= 1 - Page is in use by 1 or more threads. Page must run "Release" when finished using
        /// </summary>
        private readonly ConcurrentDictionary<long, PageBuffer> _readable = new ConcurrentDictionary<long, PageBuffer>();

        /// <summary>
        /// Get memory segment sizes
        /// </summary>
        private readonly int[] _segmentSizes;

        private readonly List<int> _segments = new List<int>();

        private readonly int _maxPageCount;

        private int _totalPages = 0;

        public MemoryCache(int[] memorySegmentSizes, int maxPageCount = int.MaxValue)
        {
            _segmentSizes = memorySegmentSizes ?? throw new ArgumentNullException(nameof(memorySegmentSizes));
            _maxPageCount = maxPageCount <= 0 ? int.MaxValue : maxPageCount;

            var firstSize = _segmentSizes.Length > 0 ? _segmentSizes[0] : 1;
            var initialSize = Math.Min(firstSize, _maxPageCount);
            initialSize = Math.Max(1, initialSize);

            if (this.AllocateSegment(initialSize) == false)
            {
                throw LiteException.CacheLimitExceeded((long)_maxPageCount * PAGE_SIZE);
            }
        }

        #region Readable Pages

        /// <summary>
        /// Get page from clean cache (readable). If page doesn't exist, create this new page and load data using factory fn
        /// </summary>
        public PageBuffer GetReadablePage(long position, FileOrigin origin, Action<long, BufferSlice> factory)
        {
            // get dict key based on position/origin
            var key = this.GetReadableKey(position, origin);

            // try get from _readble dict or create new
            var page = _readable.GetOrAdd(key, (k) =>
            {
                // get new page from _free pages (or extend)
                var newPage = this.GetFreePage();

                newPage.Position = position;
                newPage.Origin = origin;

                // load page content with disk stream
                factory(position, newPage);

                return newPage;
            });

            // update LRU
            Interlocked.Exchange(ref page.Timestamp, DateTime.UtcNow.Ticks);

            // increment share counter
            Interlocked.Increment(ref page.ShareCounter);

            return page;
        }

        /// <summary>
        /// Get unique position in dictionary according with origin. Use positive/negative values
        /// </summary>
        private long GetReadableKey(long position, FileOrigin origin)
        {
            ENSURE(origin != FileOrigin.None, "file origin must be defined");

            if (origin == FileOrigin.Data)
            {
                return position;
            }
            else
            {
                if (position == 0) return long.MinValue;

                return -position;
            }
        }

        #endregion

        #region Writable Pages

        /// <summary>
        /// Request for a writable page - no other can read this page and this page has no reference
        /// Writable pages can be MoveToReadable() or DiscardWritable() - but never Released()
        /// </summary>
        public PageBuffer GetWritablePage(long position, FileOrigin origin, Action<long, BufferSlice> factory)
        {
            var key = this.GetReadableKey(position, origin);

            // write pages always contains a new buffer array
            var writable = this.NewPage(position, origin);

            // if requested page already in cache, just copy buffer and avoid load from stream
            if (_readable.TryGetValue(key, out var clean))
            {
                Buffer.BlockCopy(clean.Array, clean.Offset, writable.Array, writable.Offset, PAGE_SIZE);
            }
            else
            {
                factory(position, writable);
            }

            return writable;
        }

        /// <summary>
        /// Create new page using an empty buffer block. Mark this page as writable.
        /// </summary>
        public PageBuffer NewPage()
        {
            return this.NewPage(long.MaxValue, FileOrigin.None);
        }

        /// <summary>
        /// Create new page using an empty buffer block. Mark this page as writable.
        /// </summary>
        private PageBuffer NewPage(long position, FileOrigin origin)
        {
            var page = this.GetFreePage();

            // set page position and page as writable
            page.Position = position;

            // define as writable
            page.ShareCounter = BUFFER_WRITABLE;

            // Timestamp = 0 means this page was never used (do not clear)
            if (page.Timestamp > 0)
            {
                page.Clear();
            }

            DEBUG(page.All(0), "new page must be full zero empty before return");

            page.Origin = origin;
            page.Timestamp = DateTime.UtcNow.Ticks;

            return page;
        }

        /// <summary>
        /// Try to move this page to readable list (if not already in readable list)
        /// Returns true if it was moved
        /// </summary>
        public bool TryMoveToReadable(PageBuffer page)
        {
            ENSURE(page.Position != long.MaxValue, "page must have a position");
            ENSURE(page.ShareCounter == BUFFER_WRITABLE, "page must be writable");
            ENSURE(page.Origin != FileOrigin.None, "page must have origin defined");

            var key = this.GetReadableKey(page.Position, page.Origin);

            // set page as not in use
            page.ShareCounter = 0;

            var added = _readable.TryAdd(key, page);

            // if not added, let's get ShareCounter back to writable state
            if (!added)
            {
                page.ShareCounter = BUFFER_WRITABLE;
            }

            return added;
        }

        /// <summary>
        /// Move a writable page to readable list - if already exists, override content
        /// Used after write operation that must mark page as readable because page content was changed
        /// This method runs BEFORE send to write disk queue - but new page request must read this new content
        /// Returns readable page
        /// </summary>
        public PageBuffer MoveToReadable(PageBuffer page)
        {
            ENSURE(page.Position != long.MaxValue, "page must have position to be readable");
            ENSURE(page.Origin != FileOrigin.None, "page should be a source before move to readable");
            ENSURE(page.ShareCounter == BUFFER_WRITABLE, "page must be writable before move to readable dict");

            var key = this.GetReadableKey(page.Position, page.Origin);
            var added = true;

            // no concurrency in writable page
            page.ShareCounter = 1;

            var readable = _readable.AddOrUpdate(key, page, (newKey, current) =>
            {
                // if page already exist inside readable list, should never be in-used (this will be guaranteed by lock control)
                ENSURE(current.ShareCounter == 0, "user must ensure this page is not in use when marked as read only");
                ENSURE(current.Origin == page.Origin, "origin must be same");

                current.ShareCounter = 1;

                // if page already in cache, this is a duplicate page in memory
                // must update cached page with new page content
                Buffer.BlockCopy(page.Array, page.Offset, current.Array, current.Offset, PAGE_SIZE);

                added = false;

                // Bug 2184: readable page was updated, need to set the page.ShareCounter back to writeable
                // so that DiscardPage can free the page and put it into the queue.
                page.ShareCounter = BUFFER_WRITABLE;

                return current;
            });

            // if page was not added into readable list, move page to free list
            if (added == false)
            {
                this.DiscardPage(page);
            }

            // return page that are in _readable list
            return readable;
        }

        /// <summary>
        /// Completely discard a writable page - clean content and move to free list
        /// </summary>
        public void DiscardPage(PageBuffer page)
        {
            ENSURE(page.ShareCounter == BUFFER_WRITABLE, "discarded page must be writable");

            // clear page controls
            page.ShareCounter = 0;
            page.Position = long.MaxValue;
            page.Origin = FileOrigin.None;

            // DO NOT CLEAR CONTENT
            // when this page get requested from free list, it will be cleared if requested from NewPage()
            //  or will be overwritten by ReadPage

            // added into free list
            _free.Enqueue(page);
        }

        #endregion

        #region Cache managment

        /// <summary>
        /// Get a clean, re-usable page from store. Can extend buffer segments if store is empty
        /// </summary>
        private PageBuffer GetFreePage()
        {
            if (_free.TryDequeue(out var page))
            {
                return Validate(page);
            }

            lock(_free)
            {
                if (_free.TryDequeue(out page))
                {
                    return Validate(page);
                }

                var spinner = new SpinWait();

                for (var attempt = 0; attempt < 4; attempt++)
                {
                    if (this.TryExtend())
                    {
                        return this.GetFreePage();
                    }

                    if (_maxPageCount == int.MaxValue)
                    {
                        break;
                    }

                    spinner.SpinOnce();
                }

                throw LiteException.CacheLimitExceeded((long)_maxPageCount * PAGE_SIZE);
            }

            static PageBuffer Validate(PageBuffer page)
            {
                ENSURE(page.Position == long.MaxValue, "pages in memory store must have no position defined");
                ENSURE(page.ShareCounter == 0, "pages in memory store must be non-shared");
                ENSURE(page.Origin == FileOrigin.None, "page in memory must have no page origin");

                return page;
            }
        }

        /// <summary>
        /// Check if it's possible move readable pages to free list - if not possible, extend memory
        /// </summary>
        private bool TryExtend()
        {
            var patternIndex = _segmentSizes.Length == 0 ? 0 : Math.Min(_segmentSizes.Length - 1, _segments.Count);
            var patternSize = _segmentSizes.Length == 0 ? 1 : _segmentSizes[patternIndex];

            var remaining = _maxPageCount - _totalPages;
            if (remaining <= 0)
            {
                return this.Reclaim(patternSize) > 0;
            }

            var target = Math.Min(patternSize, remaining);

            if (this.Reclaim(target) > 0)
            {
                return true;
            }

            return this.AllocateSegment(target);
        }

        private int Reclaim(int request)
        {
            if (request <= 0)
            {
                return 0;
            }

            var reclaimed = 0;

            var readables = _readable
                .Where(x => x.Value.ShareCounter == 0)
                .OrderBy(x => x.Value.Timestamp)
                .Select(x => x.Key)
                .Take(request)
                .ToArray();

            foreach (var key in readables)
            {
                if (_readable.TryRemove(key, out var page) == false)
                {
                    continue;
                }

                if (page.ShareCounter > 0)
                {
                    if (!_readable.TryAdd(key, page))
                    {
                        throw new LiteException(0, "MemoryCache: removed in-use memory page. This situation has no way to fix (yet). Throwing exception to avoid database corruption. No other thread can read/write from database now.");
                    }

                    continue;
                }

                ENSURE(page.ShareCounter == 0, "page should not be in use by anyone");

                page.Position = long.MaxValue;
                page.Origin = FileOrigin.None;

                _free.Enqueue(page);
                reclaimed++;
            }

            if (reclaimed > 0)
            {
                LOG($"re-using cache pages (flushing {reclaimed} pages)", "CACHE");
            }

            return reclaimed;
        }

        private bool AllocateSegment(int segmentSize)
        {
            if (segmentSize <= 0)
            {
                return false;
            }

            var buffer = new byte[PAGE_SIZE * segmentSize];
            var uniqueID = _totalPages + 1;

            for (var i = 0; i < segmentSize; i++)
            {
                _free.Enqueue(new PageBuffer(buffer, i * PAGE_SIZE, uniqueID++));
            }

            _segments.Add(segmentSize);
            _totalPages += segmentSize;

            LOG($"extending memory usage: (segments: {_segments.Count})", "CACHE");

            return true;
        }

        /// <summary>
        /// Return how many pages are in use when call this method (ShareCounter != 0).
        /// </summary>
        public int PagesInUse => _readable.Values.Where(x => x.ShareCounter != 0).Count();

        /// <summary>
        /// Return how many pages are available (completely free)
        /// </summary>
        public int FreePages => _free.Count;

        /// <summary>
        /// Return how many segments are already loaded in memory
        /// </summary>
        public int ExtendSegments => _segments.Count;

        /// <summary>
        /// Get how many pages this cache extends in memory
        /// </summary>
        public int ExtendPages => _totalPages;

        /// <summary>
        /// Get how many pages are used as Writable at this moment
        /// </summary>
        public int WritablePages => _totalPages -
            _free.Count - _readable.Count;

        /// <summary>
        /// Get all readable pages
        /// </summary>
        public ICollection<PageBuffer> GetPages() => _readable.Values;

        /// <summary>
        /// Clean all cache memory - moving back all readable pages into free list
        /// This command must be called inside an exclusive lock
        /// </summary>
        public int Clear()
        {
            var counter = 0;

            ENSURE(this.PagesInUse == 0, "must have no pages in use when call Clear() cache");

            foreach (var page in _readable.Values)
            {
                page.Position = long.MaxValue;
                page.Origin = FileOrigin.None;

                _free.Enqueue(page);

                counter++;
            }

            _readable.Clear();

            return counter;
        }

        #endregion

        public void Dispose()
        {
        }
    }
}