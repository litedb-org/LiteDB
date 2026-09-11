using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal sealed class MemoryCacheSegment
    {
        public byte[] Buffer;
        public readonly PageBuffer[] Frames;
        public int FreeCount;
        public int FreeHead;
        public int Busy;
        public int Bucket = -1;

        public MemoryCacheSegment(byte[] buffer, PageBuffer[] frames)
        {
            this.Buffer = buffer;
            this.Frames = frames;
            this.FreeCount = frames.Length;
            this.FreeHead = frames.Length == 0 ? -1 : 0;
        }
    }

    /// <summary>
    /// Bounded, elastic page-buffer pool. One lock protects the readable
    /// index, frame states, pins, segment free lists, and segment liveness.
    /// Disk reads deliberately run outside the lock.
    /// </summary>
    internal sealed class MemoryCache : IDisposable
    {
        internal const int DEFAULT_EVICT_SCAN_BUDGET = 256;

        private readonly object _sync = new object();
        private readonly Dictionary<long, PageBuffer> _index = new Dictionary<long, PageBuffer>();
        private readonly List<MemoryCacheSegment> _segments = new List<MemoryCacheSegment>();
        private readonly HashSet<MemoryCacheSegment>[] _freeBuckets =
        {
            new HashSet<MemoryCacheSegment>(),
            new HashSet<MemoryCacheSegment>(),
            new HashSet<MemoryCacheSegment>(),
            new HashSet<MemoryCacheSegment>(),
            new HashSet<MemoryCacheSegment>()
        };
        private readonly HashSet<MemoryCacheSegment> _releasableSegments = new HashSet<MemoryCacheSegment>();
        private readonly int[] _segmentSizes;
        private readonly int _evictScanBudget;
        private MemoryCacheSegment _currentSegment;
        private int _segmentsAllocated;
        private int _nextUniqueID;
        private int _clockSegment;
        private int _clockFrame;
        private bool _disposed;

        private int _totalPages;
        private int _freePages;
        private int _readablePages;
        private int _idleReadablePages;
        private int _writablePages;
        private int _loadingPages;
        private int _pinnedPages;
        private int _fullyFreeSegments;

        private long _evictedPages;
        private long _releasedSegments;
        private long _overflowSegments;
        private long _framesExamined;
        private long _budgetExceeded;
        private long _hits;
        private long _misses;

#if TESTING
        internal Action ReadableHitUnderLock { get; set; }
        internal Action WritableCopyUnderLock { get; set; }
        internal Action LoadingWaiterWaiting { get; set; }
        internal Action<Action> LoadingWaiterResuming { get; set; }
#endif

        public MemoryCache(int[] memorySegmentSizes)
            : this(memorySegmentSizes, long.MaxValue)
        {
        }

        public MemoryCache(int[] memorySegmentSizes, long cacheSize, int evictScanBudget = DEFAULT_EVICT_SCAN_BUDGET)
        {
            if (memorySegmentSizes == null) throw new ArgumentNullException(nameof(memorySegmentSizes));
            if (memorySegmentSizes.Length == 0 || memorySegmentSizes.Any(x => x <= 0))
            {
                throw new ArgumentException("Memory segment sizes must contain positive values", nameof(memorySegmentSizes));
            }
            if (evictScanBudget <= 0) throw new ArgumentOutOfRangeException(nameof(evictScanBudget));

            _segmentSizes = (int[])memorySegmentSizes.Clone();
            _evictScanBudget = evictScanBudget;
            this.LimitBytes = cacheSize <= 0 ? PAGE_SIZE * (long)this.MinimumPages : cacheSize;
            this.LimitPagesRounded = this.RoundLimitPages(this.LimitBytes);

            lock (_sync)
            {
                this.AllocateSegmentLocked(false);
            }
        }

        public long LimitBytes { get; }
        public int LimitPagesRounded { get; }
        public int EvictScanBudget => _evictScanBudget;

        public PageBuffer GetReadablePage(long position, FileOrigin origin, Action<long, BufferSlice> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));

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
                        _hits++;
                        return page;
                    }

                    page = this.AcquireFrameLocked();
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
                page.Timestamp = DateTime.UtcNow.Ticks;

                Monitor.PulseAll(_sync);
                return page;
            }
        }

        private long GetReadableKey(long position, FileOrigin origin)
        {
            ENSURE(origin != FileOrigin.None, "file origin must be defined");

            if (origin == FileOrigin.Data) return position;
            return position == 0 ? long.MinValue : -position;
        }

        public PageBuffer GetWritablePage(long position, FileOrigin origin, Action<long, BufferSlice> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            var key = this.GetReadableKey(position, origin);
            PageBuffer writable;

            lock (_sync)
            {
                this.ThrowIfDisposedLocked();

                while (_index.TryGetValue(key, out var loading) && loading.State == FrameState.Loading)
                {
                    Monitor.Wait(_sync);
                    this.ThrowIfDisposedLocked();
                }

                writable = this.AcquireWritableLocked(position, origin);

                if (_index.TryGetValue(key, out var readable))
                {
                    ENSURE(readable.State == FrameState.Readable, "cached source must be readable");
#if TESTING
                    WritableCopyUnderLock?.Invoke();
#endif
                    Buffer.BlockCopy(readable.Array, readable.Offset, writable.Array, writable.Offset, PAGE_SIZE);
                    readable.Referenced = 1;
                    _hits++;
                    return writable;
                }

                _misses++;
            }

            try
            {
                factory(position, writable);
                return writable;
            }
            catch
            {
                this.DiscardPage(writable);
                throw;
            }
        }

        public PageBuffer NewPage()
        {
            lock (_sync)
            {
                this.ThrowIfDisposedLocked();
                return this.AcquireWritableLocked(long.MaxValue, FileOrigin.None);
            }
        }

        private PageBuffer AcquireWritableLocked(long position, FileOrigin origin)
        {
            var page = this.AcquireFrameLocked();

            page.Position = position;
            page.Origin = origin;
            page.State = FrameState.Writable;
            page.ShareCounter = BUFFER_WRITABLE;
            page.Referenced = 0;
            page.Timestamp = DateTime.UtcNow.Ticks;
            this.ChangeBusyLocked(page.Segment, 1);
            _writablePages++;

            page.Clear();

            DEBUG(page.All(0), "new page must be full zero empty before return");

            return page;
        }

        public bool TryMoveToReadable(PageBuffer page)
        {
            lock (_sync)
            {
                this.EnsureWritableOwnedLocked(page);
                ENSURE(page.Position != long.MaxValue, "page must have a position");
                ENSURE(page.Origin != FileOrigin.None, "page must have origin defined");

                var key = this.GetReadableKey(page.Position, page.Origin);

                if (_index.ContainsKey(key)) return false;

                _index.Add(key, page);
                page.State = FrameState.Readable;
                page.ShareCounter = 0;
                page.Referenced = 1;
                page.Timestamp = DateTime.UtcNow.Ticks;
                this.ChangeBusyLocked(page.Segment, -1);
                _writablePages--;
                _readablePages++;
                _idleReadablePages++;

                return true;
            }
        }

        public PageBuffer MoveToReadable(PageBuffer page)
        {
            lock (_sync)
            {
                this.EnsureWritableOwnedLocked(page);
                ENSURE(page.Position != long.MaxValue, "page must have position to be readable");
                ENSURE(page.Origin != FileOrigin.None, "page should be a source before move to readable");

                var key = this.GetReadableKey(page.Position, page.Origin);
                ENSURE(!_index.ContainsKey(key), "writable page position must not already exist in readable cache");

                _index.Add(key, page);
                page.State = FrameState.Readable;
                page.ShareCounter = 1;
                page.Referenced = 1;
                page.Timestamp = DateTime.UtcNow.Ticks;
                _writablePages--;
                _readablePages++;
                _pinnedPages++;

                return page;
            }
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
            lock (_sync)
            {
                ENSURE(!_disposed, "cannot release a page from a disposed cache");
                ENSURE(ReferenceEquals(page.Cache, this), "page must belong to this cache");
                ENSURE(page.State == FrameState.Readable, "only readable pages can be released");
                ENSURE(page.ShareCounter > 0, "share counter must be > 0 in Release()");

                page.ShareCounter--;

                if (page.ShareCounter == 0)
                {
                    this.ChangeBusyLocked(page.Segment, -1);
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
                this.ChangeBusyLocked(page.Segment, 1);
                _idleReadablePages--;
                _pinnedPages++;
            }

            page.ShareCounter++;
            page.Referenced = 1;
            page.Timestamp = DateTime.UtcNow.Ticks;
        }

        private void TransitionFreeToLoadingLocked(PageBuffer page, long position, FileOrigin origin)
        {
            ENSURE(page.State == FrameState.Free, "only a free frame can begin loading");

            page.Position = position;
            page.Origin = origin;
            page.State = FrameState.Loading;
            page.ShareCounter = 0;
            page.Referenced = 0;
            this.ChangeBusyLocked(page.Segment, 1);
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
                    this.ChangeBusyLocked(segment, -1);
                    break;
                case FrameState.Readable:
                    ENSURE(page.ShareCounter == 0, "pinned readable page cannot become free");
                    _readablePages--;
                    _idleReadablePages--;
                    break;
                case FrameState.Writable:
                    _writablePages--;
                    this.ChangeBusyLocked(segment, -1);
                    break;
                default:
                    ENSURE(false, "free frame cannot be returned twice");
                    break;
            }

            page.State = FrameState.Free;
            page.ShareCounter = 0;
            page.Position = long.MaxValue;
            page.Origin = FileOrigin.None;
            page.Referenced = 0;
            page.Timestamp = 0;
            page.Generation++;

            for (var i = 0; i < page.Count; i++)
            {
                page.Array[page.Offset + i] = 0xFF;
            }

            this.AddFreeFrameLocked(segment, page);
        }

        private PageBuffer AcquireFrameLocked()
        {
            while (true)
            {
                if (_currentSegment != null && _currentSegment.FreeCount > 0)
                {
                    return this.TakeFreeFrameLocked(_currentSegment);
                }

                _currentSegment = this.SelectPopulatedFreeSegmentLocked();

                if (_currentSegment != null)
                {
                    return this.TakeFreeFrameLocked(_currentSegment);
                }

                if (_totalPages < this.LimitPagesRounded)
                {
                    _currentSegment = this.AllocateSegmentLocked(false);
                    return this.TakeFreeFrameLocked(_currentSegment);
                }

                if (_idleReadablePages == 0)
                {
                    _currentSegment = this.AllocateSegmentLocked(true);
                    return this.TakeFreeFrameLocked(_currentSegment);
                }

                var examined = this.ClockUntilVictimLocked();
                _framesExamined += examined;
                if (examined > _evictScanBudget) _budgetExceeded++;

                ENSURE(_freePages > 0, "idle readable accounting promised an eviction victim");
            }
        }

        private int ClockUntilVictimLocked()
        {
            var examined = 0;
            var maximum = Math.Max(1, _totalPages * 2);

            while (examined < maximum)
            {
                var page = this.NextClockFrameLocked();
                examined++;

                if (page.State != FrameState.Readable || page.ShareCounter != 0) continue;

                if (page.Referenced != 0)
                {
                    page.Referenced = 0;
                    continue;
                }

                this.EvictLocked(page);
                return examined;
            }

            return examined;
        }

        private PageBuffer NextClockFrameLocked()
        {
            ENSURE(_segments.Count > 0, "cache must contain a segment");

            if (_clockSegment >= _segments.Count)
            {
                _clockSegment = 0;
                _clockFrame = 0;
            }

            var segment = _segments[_clockSegment];
            var page = segment.Frames[_clockFrame++];

            if (_clockFrame >= segment.Frames.Length)
            {
                _clockFrame = 0;
                _clockSegment++;
                if (_clockSegment >= _segments.Count) _clockSegment = 0;
            }

            return page;
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

        private PageBuffer TakeFreeFrameLocked(MemoryCacheSegment segment)
        {
            ENSURE(segment.FreeHead >= 0 && segment.FreeCount > 0, "segment must contain a free frame");

            this.RemoveFromBucketLocked(segment);

            if (segment.FreeCount == segment.Frames.Length)
            {
                _fullyFreeSegments--;
            }

            var index = segment.FreeHead;
            var page = segment.Frames[index];
            segment.FreeHead = page.NextFree;
            segment.FreeCount--;
            page.NextFree = -1;
            _freePages--;

            this.AddToBucketLocked(segment);

            ENSURE(page.State == FrameState.Free, "free-list frame must be free");
            ENSURE(page.Position == long.MaxValue, "free-list frame must have no position");
            ENSURE(page.ShareCounter == 0, "free-list frame must be unpinned");
            ENSURE(page.Origin == FileOrigin.None, "free-list frame must have no origin");

            page.RefreshOwnerGeneration();

            return page;
        }

        private void AddFreeFrameLocked(MemoryCacheSegment segment, PageBuffer page)
        {
            this.RemoveFromBucketLocked(segment);

            var index = page.Offset / PAGE_SIZE;
            page.NextFree = segment.FreeHead;
            segment.FreeHead = index;
            segment.FreeCount++;
            _freePages++;

            if (segment.FreeCount == segment.Frames.Length)
            {
                _fullyFreeSegments++;
            }

            this.AddToBucketLocked(segment);

            if (_currentSegment == null || _currentSegment.FreeCount == 0)
            {
                _currentSegment = segment;
            }
        }

        private MemoryCacheSegment SelectPopulatedFreeSegmentLocked()
        {
            for (var i = 0; i < _freeBuckets.Length; i++)
            {
                foreach (var segment in _freeBuckets[i])
                {
                    if (segment.FreeCount > 0) return segment;
                }
            }

            return null;
        }

        private void AddToBucketLocked(MemoryCacheSegment segment)
        {
            if (segment.FreeCount == 0)
            {
                segment.Bucket = -1;
                return;
            }

            var bucket = segment.FreeCount == segment.Frames.Length ? 4 :
                segment.FreeCount <= 15 ? 0 :
                segment.FreeCount <= 63 ? 1 :
                segment.FreeCount <= 127 ? 2 : 3;

            segment.Bucket = bucket;
            _freeBuckets[bucket].Add(segment);
        }

        private void RemoveFromBucketLocked(MemoryCacheSegment segment)
        {
            if (segment.Bucket >= 0)
            {
                _freeBuckets[segment.Bucket].Remove(segment);
                segment.Bucket = -1;
            }
        }

        private MemoryCacheSegment AllocateSegmentLocked(bool overflow)
        {
            var segmentSize = _segmentSizes[Math.Min(_segmentSizes.Length - 1, _segmentsAllocated)];
            var buffer = new byte[PAGE_SIZE * segmentSize];
            var frames = new PageBuffer[segmentSize];
            var segment = new MemoryCacheSegment(buffer, frames);

            for (var i = 0; i < segmentSize; i++)
            {
                var page = new PageBuffer(buffer, i * PAGE_SIZE, ++_nextUniqueID)
                {
                    Cache = this,
                    Segment = segment,
                    NextFree = i + 1 < segmentSize ? i + 1 : -1
                };

                frames[i] = page;
            }

            _segments.Add(segment);
            _releasableSegments.Add(segment);
            _segmentsAllocated++;
            _totalPages += segmentSize;
            _freePages += segmentSize;
            _fullyFreeSegments++;
            if (overflow) _overflowSegments++;
            this.AddToBucketLocked(segment);

            LOG($"extending memory usage: (segments: {_segments.Count})", "CACHE");
            return segment;
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

                this.ReleaseFullyFreeSegmentsLocked(this.LimitPagesRounded, true);
                return pages.Length;
            }
        }

        public int Clear() => this.Invalidate();

        public void TrimToLimit()
        {
            lock (_sync)
            {
                if (_disposed) return;

                while (_totalPages > this.LimitPagesRounded)
                {
                    var before = _totalPages;
                    var evicted = 0;

                    this.ReleaseFullyFreeSegmentsLocked(this.LimitPagesRounded, true);
                    if (_totalPages <= this.LimitPagesRounded) break;

                    // Empty non-initial segments are the only ones that can be
                    // returned to the GC. Prefer them before disturbing the
                    // initial segment or partially pinned segments.
                    MemoryCacheSegment candidate = null;

                    foreach (var segment in _releasableSegments)
                    {
                        if (segment.FreeCount == segment.Frames.Length) continue;

                        // Preserve the small initial segment when any later
                        // segment can be emptied instead.
                        if (ReferenceEquals(segment, _segments[0]))
                        {
                            candidate ??= segment;
                        }
                        else
                        {
                            candidate = segment;
                            break;
                        }
                    }

                    if (candidate != null)
                    {
                        foreach (var page in candidate.Frames)
                        {
                            if (page.State == FrameState.Readable && page.ShareCounter == 0)
                            {
                                this.EvictLocked(page);
                                evicted++;
                            }
                        }
                    }

                    this.ReleaseFullyFreeSegmentsLocked(this.LimitPagesRounded, true);

                    // A first candidate can become the one retained spare
                    // without reducing TotalPages. Continue only when an
                    // eviction made progress; otherwise every remaining
                    // excess segment is pinned, writable, or loading.
                    if (_totalPages == before && evicted == 0) break;
                }
            }
        }

        private void ReleaseFullyFreeSegmentsLocked(int downTo, bool keepSpare)
        {
            for (var i = _segments.Count - 1; i > 0 && _totalPages > downTo; i--)
            {
                var segment = _segments[i];

                if (segment.FreeCount != segment.Frames.Length) continue;
                if (keepSpare && _fullyFreeSegments <= 1) break;

                this.RemoveFromBucketLocked(segment);
                _releasableSegments.Remove(segment);
                if (ReferenceEquals(_currentSegment, segment)) _currentSegment = null;

                _segments.RemoveAt(i);
                _totalPages -= segment.Frames.Length;
                _freePages -= segment.Frames.Length;
                _fullyFreeSegments--;
                _releasedSegments++;

                foreach (var page in segment.Frames)
                {
                    page.Cache = null;
                    page.Segment = null;
                    page.NextFree = -1;
                }

                segment.Buffer = null;
                _clockSegment = 0;
                _clockFrame = 0;
            }
        }

        private int MinimumPages
        {
            get
            {
                var first = _segmentSizes[0];
                var second = _segmentSizes[Math.Min(1, _segmentSizes.Length - 1)];
                return checked(first + second);
            }
        }

        private int RoundLimitPages(long cacheSize)
        {
            if (cacheSize == long.MaxValue) return int.MaxValue;

            var requestedLong = (cacheSize / PAGE_SIZE) + (cacheSize % PAGE_SIZE == 0 ? 0 : 1);
            var requested = Math.Max(this.MinimumPages, (int)Math.Min(int.MaxValue, requestedLong));
            var pages = 0;
            var segment = 0;

            while (pages < requested)
            {
                var size = _segmentSizes[Math.Min(_segmentSizes.Length - 1, segment++)];
                if (pages > int.MaxValue - size) return int.MaxValue;
                pages += size;
            }

            return pages;
        }

        private void EnsureWritableOwnedLocked(PageBuffer page)
        {
            if (page == null) throw new ArgumentNullException(nameof(page));
            ENSURE(ReferenceEquals(page.Cache, this), "page must belong to this cache");
            ENSURE(page.State == FrameState.Writable, "page must be writable");
            ENSURE(page.ShareCounter == BUFFER_WRITABLE, "writable page must use writable share marker");
        }

        private void ChangeBusyLocked(MemoryCacheSegment segment, int delta)
        {
            ENSURE(segment != null, "busy frame must belong to an active segment");
            ENSURE(delta == -1 || delta == 1, "busy count changes one frame at a time");

            if (segment.Busy == 0)
            {
                ENSURE(delta > 0, "segment busy count cannot become negative");
                _releasableSegments.Remove(segment);
            }

            segment.Busy += delta;
            ENSURE(segment.Busy >= 0 && segment.Busy <= segment.Frames.Length, "invalid segment busy count");

            if (segment.Busy == 0)
            {
                _releasableSegments.Add(segment);
            }
        }

        private void ThrowIfDisposedLocked()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryCache));
        }

        public int PagesInUse { get { lock (_sync) return _pinnedPages; } }
        public int PinnedPages { get { lock (_sync) return _pinnedPages; } }
        public int FreePages { get { lock (_sync) return _freePages; } }
        public int ExtendSegments { get { lock (_sync) return _segments.Count; } }
        public int Segments => this.ExtendSegments;
        public int ExtendPages { get { lock (_sync) return _totalPages; } }
        public int TotalPages => this.ExtendPages;
        public long AllocatedBytes { get { lock (_sync) return _totalPages * (long)PAGE_SIZE; } }
        public int WritablePages { get { lock (_sync) return _writablePages; } }
        public int LoadingPages { get { lock (_sync) return _loadingPages; } }
        public int ReadablePages { get { lock (_sync) return _readablePages; } }
        public int IdleReadablePages { get { lock (_sync) return _idleReadablePages; } }
        public long EvictedPages { get { lock (_sync) return _evictedPages; } }
        public long ReleasedSegments { get { lock (_sync) return _releasedSegments; } }
        public long OverflowSegments { get { lock (_sync) return _overflowSegments; } }
        public long FramesExamined { get { lock (_sync) return _framesExamined; } }
        public long BudgetExceeded { get { lock (_sync) return _budgetExceeded; } }
        public long Hits { get { lock (_sync) return _hits; } }
        public long Misses { get { lock (_sync) return _misses; } }
        public long LostFrames
        {
            get
            {
                lock (_sync)
                {
                    return _totalPages - (long)_freePages - _readablePages - _writablePages - _loadingPages;
                }
            }
        }

        public int RetainedBySegments
        {
            get
            {
                lock (_sync)
                {
                    return _segments.Where(x => x.Busy > 0).Sum(x => x.Frames.Length - x.Busy);
                }
            }
        }

        public ICollection<PageBuffer> GetPages()
        {
            lock (_sync) return _index.Values.ToArray();
        }

        internal ICollection<WeakReference> GetSegmentWeakReferences()
        {
            lock (_sync) return _segments.Select(x => new WeakReference(x.Buffer)).ToArray();
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;

                foreach (var segment in _segments)
                {
                    if (segment.Buffer != null)
                    {
                        for (var i = 0; i < segment.Buffer.Length; i++)
                        {
                            segment.Buffer[i] = 0xFF;
                        }
                    }

                    foreach (var page in segment.Frames)
                    {
                        page.ShareCounter = 0;
                        page.State = FrameState.Free;
                        page.Position = long.MaxValue;
                        page.Origin = FileOrigin.None;
                        page.Referenced = 0;
                        page.Generation++;
                        page.Cache = null;
                        page.Segment = null;
                        page.NextFree = -1;
                    }
                    segment.Buffer = null;
                }

                _index.Clear();
                _segments.Clear();
                foreach (var bucket in _freeBuckets) bucket.Clear();
                _releasableSegments.Clear();
                _currentSegment = null;
                _totalPages = 0;
                _freePages = 0;
                _readablePages = 0;
                _idleReadablePages = 0;
                _writablePages = 0;
                _loadingPages = 0;
                _pinnedPages = 0;
                _fullyFreeSegments = 0;
                Monitor.PulseAll(_sync);
            }
        }
    }
}
