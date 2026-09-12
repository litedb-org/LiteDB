using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Bounded, elastic page-buffer pool. One lock protects the readable
    /// index, idle/busy transitions, free lists, and segment liveness. Readers
    /// share existing pins atomically; disk reads run outside the lock.
    /// </summary>
    internal sealed class MemoryCache : IDisposable
    {
        internal const int DEFAULT_EVICT_SCAN_BUDGET = 256;

        private readonly object _sync = new object();
        private readonly PageFramePool _pool;
        private readonly CacheReclaimer _reclaimer;
        private readonly SharedPageReads _sharedReads = new SharedPageReads();
        private readonly Dictionary<long, PageBuffer> _index = new Dictionary<long, PageBuffer>();
        private bool _disposed;

        private int _readablePages;
        private int _idleReadablePages;
        private int _writablePages;
        private int _loadingPages;
        private int _pinnedPages;
        private long _evictedPages;
        private long _hits;
        private long _misses;

#if TESTING
        internal Action ReadableHitUnderLock { get; set; }
        internal Action BeforeWritableCopy { get; set; }
        internal Action LoadingWaiterWaiting { get; set; }
        internal Action<Action> LoadingWaiterResuming { get; set; }
#endif

        public MemoryCache(int[] memorySegmentSizes, long cacheSize, int evictScanBudget = DEFAULT_EVICT_SCAN_BUDGET)
        {
            if (evictScanBudget <= 0) throw new ArgumentOutOfRangeException(nameof(evictScanBudget));

            _pool = new PageFramePool(this, memorySegmentSizes);
            this.LimitBytes = cacheSize <= 0 ? PAGE_SIZE * (long)_pool.MinimumPages : cacheSize;
            this.LimitPagesRounded = _pool.RoundLimitPages(this.LimitBytes);
            _reclaimer = new CacheReclaimer(_pool, this.LimitPagesRounded, evictScanBudget,
                () => _idleReadablePages, this.EvictLocked);

            _pool.AllocateSegmentLocked(false);
        }

        public long LimitBytes { get; }
        public int LimitPagesRounded { get; }

        public PageBuffer GetReadablePage(long position, FileOrigin origin, Action<long, BufferSlice> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            if (Volatile.Read(ref _disposed)) throw new ObjectDisposedException(nameof(MemoryCache));
#if TESTING
            if (ReadableHitUnderLock == null)
#endif
            {
                var shared = _sharedReads.TryPin(position, origin);
                if (shared != null) { Interlocked.Increment(ref _hits); return shared; }
            }
            var key = this.GetReadableKey(position, origin);
            PageBuffer page;

            lock (_sync)
            {
                this.ThrowIfDisposedLocked();

                while (true)
                {
                    if (_index.TryGetValue(key, out page))
                    {
                        if (page.State == FrameState.Loading)
                        {
#if TESTING
                            LoadingWaiterWaiting?.Invoke();
#endif
                            Monitor.Wait(_sync);
#if TESTING
                            LoadingWaiterResuming?.Invoke(() => Monitor.Wait(_sync));
#endif
                            this.ThrowIfDisposedLocked();
                            continue;
                        }

                        ENSURE(page.State == FrameState.Readable, "indexed page must be readable or loading");
#if TESTING
                        ReadableHitUnderLock?.Invoke();
#endif
                        this.PinLocked(page);
                        _sharedReads.Remember(page);
                        Interlocked.Increment(ref _hits);
                        return page;
                    }

                    page = _reclaimer.AcquireFrameLocked();
                    this.TransitionFreeToLoadingLocked(page, position, origin);
                    _index.Add(key, page);
                    _misses++;
                    break;
                }
            }

            try
            {
                factory(position, page);
            }
            catch
            {
                lock (_sync)
                {
                    if (_index.TryGetValue(key, out var indexed) && ReferenceEquals(indexed, page))
                    {
                        _index.Remove(key);
                    }

                    ENSURE(page.State == FrameState.Loading, "failed loader must still own its loading frame");
                    this.TransitionToFreeLocked(page);
                    Monitor.PulseAll(_sync);
                }

                throw;
            }

            lock (_sync)
            {
                ENSURE(_index.TryGetValue(key, out var indexed) && ReferenceEquals(indexed, page), "loader must own indexed frame until publication");
                ENSURE(page.State == FrameState.Loading, "loaded frame must be loading before publication");

                _loadingPages--;
                _readablePages++;
                _pinnedPages++;
                page.State = FrameState.Readable;
                page.ShareCounter = 1;
                page.Referenced = 1;
                _sharedReads.Remember(page);

                Monitor.PulseAll(_sync);
                return page;
            }
        }

        private long GetReadableKey(long position, FileOrigin origin)
        {
            ENSURE(origin != FileOrigin.None, "file origin must be defined");

            return origin == FileOrigin.Data ? position : position == 0 ? long.MinValue : -position;
        }

        public PageBuffer GetWritablePage(long position, FileOrigin origin, Action<long, BufferSlice> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            var key = this.GetReadableKey(position, origin);
            PageBuffer writable = null;
            PageBuffer readable = null;
            try
            {
                lock (_sync)
                {
                    this.ThrowIfDisposedLocked();
                    while (_index.TryGetValue(key, out var loading) && loading.State == FrameState.Loading)
                    {
                        Monitor.Wait(_sync);
                        this.ThrowIfDisposedLocked();
                    }
                    if (_index.TryGetValue(key, out readable))
                    {
                        // This pin protects the source during eviction and copying.
                        this.PinLocked(readable);
                        Interlocked.Increment(ref _hits);
                    }
                    else _misses++;
                    writable = this.AcquireWritableLocked(position, origin);
                }
                if (readable != null)
                {
#if TESTING
                    BeforeWritableCopy?.Invoke();
#endif
                    Buffer.BlockCopy(readable.Array, readable.Offset, writable.Array, writable.Offset, PAGE_SIZE);
                }
                else
                {
                    writable.Clear();
                    factory(position, writable);
                }
                return writable;
            }
            catch
            {
                if (writable != null) this.DiscardPage(writable);
                throw;
            }
            finally
            {
                readable?.Release();
            }
        }

        public PageBuffer NewPage()
        {
            PageBuffer page;
            lock (_sync)
            {
                this.ThrowIfDisposedLocked();
                page = this.AcquireWritableLocked(long.MaxValue, FileOrigin.None);
            }
            // Writable ownership keeps the frame out of eviction/reclamation.
            page.Clear();
            return page;
        }

        private PageBuffer AcquireWritableLocked(long position, FileOrigin origin)
        {
            var page = _reclaimer.AcquireFrameLocked();

            page.Position = position;
            page.Origin = origin;
            page.State = FrameState.Writable;
            page.ShareCounter = BUFFER_WRITABLE;
            page.Referenced = 0;
            _pool.ChangeBusyLocked(page.Segment, 1);
            _writablePages++;

            return page;
        }

        public bool TryMoveToReadable(PageBuffer page)
        {
            lock (_sync) return this.PublishWritableLocked(page, false);
        }

        public PageBuffer MoveToReadable(PageBuffer page)
        {
            lock (_sync)
            {
                ENSURE(this.PublishWritableLocked(page, true), "writable page position must not already exist in readable cache");
                return page;
            }
        }

        private bool PublishWritableLocked(PageBuffer page, bool pinned)
        {
            this.EnsureWritableOwnedLocked(page);
            ENSURE(page.Position != long.MaxValue, "page must have a position");
            var key = this.GetReadableKey(page.Position, page.Origin);
            if (_index.ContainsKey(key)) return false;

            _index.Add(key, page);
            page.State = FrameState.Readable;
            page.ShareCounter = pinned ? 1 : 0;
            page.Referenced = 1;
            _sharedReads.Remember(page);
            _writablePages--;
            _readablePages++;
            if (pinned)
            {
                _pinnedPages++;
            }
            else
            {
                _pool.ChangeBusyLocked(page.Segment, -1);
                _idleReadablePages++;
            }
            return true;
        }

        public void DiscardPage(PageBuffer page)
        {
            lock (_sync)
            {
                this.EnsureWritableOwnedLocked(page);
                this.TransitionToFreeLocked(page);
            }
        }

        internal void Release(PageBuffer page)
        {
            ENSURE(ReferenceEquals(page.Cache, this), "page must belong to this cache");
            if (SharedPageReads.TryReleaseShared(page)) return;
            lock (_sync)
            {
                ENSURE(!_disposed, "cannot release a page from a disposed cache");
                ENSURE(page.State == FrameState.Readable, "only readable pages can be released");
                ENSURE(page.ShareCounter > 0, "share counter must be > 0 in Release()");

                if (Interlocked.Decrement(ref page.ShareCounter) == 0)
                {
                    _pool.ChangeBusyLocked(page.Segment, -1);
                    _pinnedPages--;
                    _idleReadablePages++;
                }
            }
        }

        private void PinLocked(PageBuffer page)
        {
            ENSURE(page.State == FrameState.Readable, "only readable pages can be pinned");

            if (page.ShareCounter == 0)
            {
                _pool.ChangeBusyLocked(page.Segment, 1);
                _idleReadablePages--;
                _pinnedPages++;
            }

            Interlocked.Increment(ref page.ShareCounter);
            page.Referenced = 1;
        }

        private void TransitionFreeToLoadingLocked(PageBuffer page, long position, FileOrigin origin)
        {
            ENSURE(page.State == FrameState.Free, "only a free frame can begin loading");

            page.Position = position;
            page.Origin = origin;
            page.State = FrameState.Loading;
            page.ShareCounter = 0;
            page.Referenced = 0;
            _pool.ChangeBusyLocked(page.Segment, 1);
            _loadingPages++;
        }

        private void TransitionToFreeLocked(PageBuffer page)
        {
            var segment = page.Segment;

            ENSURE(segment != null, "page must belong to an active segment");

            switch (page.State)
            {
                case FrameState.Loading:
                    _loadingPages--;
                    _pool.ChangeBusyLocked(segment, -1);
                    break;
                case FrameState.Readable:
                    ENSURE(page.ShareCounter == 0, "pinned readable page cannot become free");
                    _readablePages--;
                    _idleReadablePages--;
                    break;
                case FrameState.Writable:
                    _writablePages--;
                    _pool.ChangeBusyLocked(segment, -1);
                    break;
                default:
                    ENSURE(false, "free frame cannot be returned twice");
                    break;
            }

            page.State = FrameState.Free;
            _sharedReads.Forget(page);
            page.ShareCounter = 0;
            page.Position = long.MaxValue;
            page.Origin = FileOrigin.None;
            page.Referenced = 0;
            page.Generation++;

#if DEBUG || TESTING
            for (var i = 0; i < page.Count; i++)
            {
                page.Array[page.Offset + i] = 0xFF;
            }
#endif

            _pool.AddFreeFrameLocked(segment, page);
        }

        private void EvictLocked(PageBuffer page)
        {
            ENSURE(page.State == FrameState.Readable && page.ShareCounter == 0, "CLOCK can only evict idle readable pages");

            var key = this.GetReadableKey(page.Position, page.Origin);
            ENSURE(_index.TryGetValue(key, out var indexed) && ReferenceEquals(indexed, page), "evicted page must be indexed");
            _index.Remove(key);
            this.TransitionToFreeLocked(page);
            _evictedPages++;
        }

        public int Invalidate()
        {
            lock (_sync)
            {
                this.ThrowIfDisposedLocked();
                ENSURE(_pinnedPages == 0, "must have no pages in use when invalidating cache");
                ENSURE(_loadingPages == 0, "must have no page loads in progress when invalidating cache");

                var pages = _index.Values.ToArray();

                foreach (var page in pages)
                {
                    ENSURE(page.State == FrameState.Readable && page.ShareCounter == 0, "checkpoint can only invalidate idle readable pages");
                    _index.Remove(this.GetReadableKey(page.Position, page.Origin));
                    this.TransitionToFreeLocked(page);
                }

                _pool.ReleaseFullyFreeSegmentsLocked(this.LimitPagesRounded, true);
                return pages.Length;
            }
        }

        public int Clear() => this.Invalidate();

        public void TrimToLimit()
        {
            // Growth after this read belongs to another active operation,
            // whose release will trim. Avoid entering the monitor on every
            // completed point lookup when no segment can be released.
            if (!_pool.ExceedsLimit(this.LimitPagesRounded)) return;

            lock (_sync)
            {
                if (_disposed) return;

                _reclaimer.TrimToLimit();
            }
        }

        private void EnsureWritableOwnedLocked(PageBuffer page)
        {
            if (page == null) throw new ArgumentNullException(nameof(page));
            ENSURE(ReferenceEquals(page.Cache, this), "page must belong to this cache");
            ENSURE(page.State == FrameState.Writable, "page must be writable");
            ENSURE(page.ShareCounter == BUFFER_WRITABLE, "writable page must use writable share marker");
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryCache));
        }

        public int PagesInUse { get { lock (_sync) return _pinnedPages; } }
        public int PinnedPages { get { lock (_sync) return _pinnedPages; } }
        public int FreePages { get { lock (_sync) return _pool.FreePages; } }
        public int ExtendSegments { get { lock (_sync) return _pool.Segments.Count; } }
        public int Segments => this.ExtendSegments;
        public int ExtendPages { get { lock (_sync) return _pool.TotalPages; } }
        public int TotalPages => this.ExtendPages;
        public long AllocatedBytes { get { lock (_sync) return _pool.TotalPages * (long)PAGE_SIZE; } }
        public int WritablePages { get { lock (_sync) return _writablePages; } }
        public int LoadingPages { get { lock (_sync) return _loadingPages; } }
        public int ReadablePages { get { lock (_sync) return _readablePages; } }
        public int IdleReadablePages { get { lock (_sync) return _idleReadablePages; } }
        public long EvictedPages { get { lock (_sync) return _evictedPages; } }
        public long ReleasedSegments { get { lock (_sync) return _pool.ReleasedSegments; } }
        public long OverflowSegments { get { lock (_sync) return _pool.OverflowSegments; } }
        public long FramesExamined { get { lock (_sync) return _reclaimer.FramesExamined; } }
        public long BudgetExceeded { get { lock (_sync) return _reclaimer.BudgetExceeded; } }
        public long Hits => Interlocked.Read(ref _hits);
        public long Misses { get { lock (_sync) return _misses; } }
        public long LostFrames
        {
            get
            {
                lock (_sync)
                {
                    return _pool.TotalPages - (long)_pool.FreePages - _readablePages - _writablePages - _loadingPages;
                }
            }
        }

        public int RetainedBySegments { get { lock (_sync) return _pool.RetainedBySegments; } }

        public ICollection<PageBuffer> GetPages()
        {
            lock (_sync) return _index.Values.ToArray();
        }

        internal ICollection<WeakReference> GetSegmentWeakReferences()
        {
            lock (_sync) return _pool.Segments.Select(x => new WeakReference(x.Buffer)).ToArray();
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _pool.EnsureIdleForDisposalLocked();
                _disposed = true;
                _sharedReads.Clear();
                _pool.Dispose();

                _index.Clear();
                _readablePages = 0;
                _idleReadablePages = 0;
                _writablePages = 0;
                _loadingPages = 0;
                _pinnedPages = 0;
                Monitor.PulseAll(_sync);
            }
        }
    }
}
