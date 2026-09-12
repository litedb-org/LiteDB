using System;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// CLOCK victim selection and segment reclamation policy. MemoryCache
    /// serializes these operations with publication and pin transitions.
    /// </summary>
    internal sealed class CacheReclaimer
    {
        private readonly PageFramePool _pool;
        private readonly int _limitPages;
        private readonly int _evictScanBudget;
        private readonly Func<int> _idleReadablePages;
        private readonly Action<PageBuffer> _evict;
        private long _framesExamined;
        private long _budgetExceeded;

        internal long FramesExamined => _framesExamined;
        internal long BudgetExceeded => _budgetExceeded;

        internal CacheReclaimer(PageFramePool pool, int limitPages, int evictScanBudget,
            Func<int> idleReadablePages, Action<PageBuffer> evict)
        {
            _pool = pool;
            _limitPages = limitPages;
            _evictScanBudget = evictScanBudget;
            _idleReadablePages = idleReadablePages;
            _evict = evict;
        }

        internal PageBuffer AcquireFrameLocked()
        {
            while (true)
            {
                if (_pool.CurrentSegment != null && _pool.CurrentSegment.FreeCount > 0)
                {
                    return _pool.TakeFreeFrameLocked(_pool.CurrentSegment);
                }

                _pool.CurrentSegment = _pool.SelectPopulatedFreeSegmentLocked();

                if (_pool.CurrentSegment != null)
                {
                    return _pool.TakeFreeFrameLocked(_pool.CurrentSegment);
                }

                if (_pool.TotalPages < _limitPages)
                {
                    _pool.CurrentSegment = _pool.AllocateSegmentLocked(false);
                    return _pool.TakeFreeFrameLocked(_pool.CurrentSegment);
                }

                if (_idleReadablePages() == 0)
                {
                    _pool.CurrentSegment = _pool.AllocateSegmentLocked(true);
                    return _pool.TakeFreeFrameLocked(_pool.CurrentSegment);
                }

                var examined = this.ClockUntilVictimLocked();
                _framesExamined += examined;
                if (examined > _evictScanBudget) _budgetExceeded++;

                ENSURE(_pool.FreePages > 0, "idle readable accounting promised an eviction victim");
            }
        }

        internal int ClockUntilVictimLocked()
        {
            var examined = 0;
            var maximum = Math.Max(1, _pool.TotalPages * 2);

            while (examined < maximum)
            {
                var page = _pool.NextClockFrameLocked();
                examined++;

                if (page.State != FrameState.Readable || page.ShareCounter != 0) continue;

                if (page.Referenced != 0)
                {
                    page.Referenced = 0;
                    continue;
                }

                _evict(page);
                return examined;
            }

            return examined;
        }

        internal void TrimToLimit()
        {
            while (_pool.TotalPages > _limitPages)
            {
                var before = _pool.TotalPages;
                var evicted = 0;

                _pool.ReleaseFullyFreeSegmentsLocked(_limitPages, true);
                if (_pool.TotalPages <= _limitPages) break;

                // Only non-initial idle segments can be returned to the GC.
                // Evicting the initial segment cannot reclaim any memory.
                MemoryCacheSegment candidate = null;

                foreach (var segment in _pool.ReleasableSegments)
                {
                    if (segment.FreeCount == segment.Frames.Length) continue;

                    if (ReferenceEquals(segment, _pool.Segments[0])) continue;

                    candidate = segment;
                    break;
                }

                if (candidate != null)
                {
                    foreach (var page in candidate.Frames)
                    {
                        if (page.State == FrameState.Readable && page.ShareCounter == 0)
                        {
                            _evict(page);
                            evicted++;
                        }
                    }
                }

                _pool.ReleaseFullyFreeSegmentsLocked(_limitPages, true);

                // A first candidate can become the one retained spare
                // without reducing TotalPages. Continue only when an
                // eviction made progress; otherwise every remaining
                // excess segment is pinned, writable, or loading.
                if (_pool.TotalPages == before && evicted == 0) break;
            }
        }
    }
}
