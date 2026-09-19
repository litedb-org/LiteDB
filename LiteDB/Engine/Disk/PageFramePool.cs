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
    /// Owns segment storage, free lists, and occupancy metadata. The enclosing
    /// MemoryCache holds its monitor for every operation except ExceedsLimit,
    /// an advisory atomic read used to skip unnecessary trimming.
    /// </summary>
    internal sealed class PageFramePool
    {
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
        private MemoryCacheSegment _currentSegment;
        private int _segmentsAllocated;
        private int _nextUniqueID;
        private int _clockSegment;
        private int _clockFrame;
        private int _totalPages;
        private int _freePages;
        private int _fullyFreeSegments;
        private long _releasedSegments;
        private long _overflowSegments;

        internal List<MemoryCacheSegment> Segments => _segments;
        internal HashSet<MemoryCacheSegment> ReleasableSegments => _releasableSegments;
        internal MemoryCacheSegment CurrentSegment { get => _currentSegment; set => _currentSegment = value; }
        internal int TotalPages => _totalPages;
        internal int FreePages => _freePages;
        internal int RetainedBySegments => _segments.Where(x => x.Busy > 0).Sum(x => x.Frames.Length - x.Busy);
        internal long ReleasedSegments => _releasedSegments;
        internal long OverflowSegments => _overflowSegments;

        private readonly MemoryCache _cache;

        internal PageFramePool(MemoryCache cache, int[] segmentSizes)
        {
            if (segmentSizes == null) throw new ArgumentNullException(nameof(segmentSizes));
            if (segmentSizes.Length == 0 || segmentSizes.Any(x => x <= 0))
            {
                throw new ArgumentException("Memory segment sizes must contain positive values", nameof(segmentSizes));
            }
            _cache = cache;
            _segmentSizes = (int[])segmentSizes.Clone();
        }

        internal bool ExceedsLimit(int limit) => Volatile.Read(ref _totalPages) > limit;

        internal PageBuffer NextClockFrameLocked()
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

        internal PageBuffer TakeFreeFrameLocked(MemoryCacheSegment segment)
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

        internal void AddFreeFrameLocked(MemoryCacheSegment segment, PageBuffer page)
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

        internal MemoryCacheSegment SelectPopulatedFreeSegmentLocked()
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

        internal void AddToBucketLocked(MemoryCacheSegment segment)
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

        internal void RemoveFromBucketLocked(MemoryCacheSegment segment)
        {
            if (segment.Bucket >= 0)
            {
                _freeBuckets[segment.Bucket].Remove(segment);
                segment.Bucket = -1;
            }
        }

        internal MemoryCacheSegment AllocateSegmentLocked(bool overflow)
        {
            var segmentSize = _segmentSizes[Math.Min(_segmentSizes.Length - 1, _segmentsAllocated)];
            var buffer = new byte[PAGE_SIZE * segmentSize];
            var frames = new PageBuffer[segmentSize];
            var segment = new MemoryCacheSegment(buffer, frames);

            for (var i = 0; i < segmentSize; i++)
            {
                var page = new PageBuffer(buffer, i * PAGE_SIZE, ++_nextUniqueID)
                {
                    Cache = _cache,
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

        internal void ReleaseFullyFreeSegmentsLocked(int downTo, bool keepSpare)
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

        internal int RoundLimitPages(long cacheSize)
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

        internal void ChangeBusyLocked(MemoryCacheSegment segment, int delta)
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

        internal int MinimumPages
        {
            get
            {
                var first = _segmentSizes[0];
                var second = _segmentSizes[Math.Min(1, _segmentSizes.Length - 1)];
                return checked(first + second);
            }
        }

        internal void EnsureIdleForDisposalLocked()
        {
            // Busy includes pinned readers, loading frames and writable pages.
            // Reject disposal before changing ownership so their operations
            // can finish and disposal can be retried without corrupting frames.
            if (_segments.Any(segment => segment.Busy != 0))
            {
                throw new InvalidOperationException(
                    "Cannot dispose the page cache while pages are pinned, loading or writable. " +
                    "Finish active operations before disposing the engine.");
            }
        }

        internal void Dispose()
        {
            foreach (var segment in _segments)
            {
#if DEBUG || TESTING
                if (segment.Buffer != null)
                {
                    for (var i = 0; i < segment.Buffer.Length; i++)
                    {
                        segment.Buffer[i] = 0xFF;
                    }
                }
#endif

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
            _segments.Clear();
            foreach (var bucket in _freeBuckets) bucket.Clear();
            _releasableSegments.Clear();
            _currentSegment = null;
            _totalPages = 0;
            _freePages = 0;
            _fullyFreeSegments = 0;
        }
    }
}
