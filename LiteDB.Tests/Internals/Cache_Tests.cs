using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class Cache_Tests
    {
        [Fact]
        public void Cache_Read_Write()
        {
            using var cache = CreateCache();
            var writable = cache.NewPage();

            writable.State.Should().Be(FrameState.Writable);
            writable.ShareCounter.Should().Be(BUFFER_WRITABLE);

            writable.Origin = FileOrigin.Log;
            writable.Position = 0;
            writable.Write(123, 10);

            cache.TryMoveToReadable(writable).Should().BeTrue();
            writable.State.Should().Be(FrameState.Readable);
            writable.ShareCounter.Should().Be(0);

            var first = cache.GetReadablePage(0, FileOrigin.Log, (_, __) => throw new InvalidOperationException());
            var second = cache.GetReadablePage(0, FileOrigin.Log, (_, __) => throw new InvalidOperationException());

            first.Should().BeSameAs(second);
            first.ReadInt32(10).Should().Be(123);
            first.ShareCounter.Should().Be(2);

            first.Release();
            second.Release();

            cache.PinnedPages.Should().Be(0);
            cache.IdleReadablePages.Should().Be(1);
            AssertAccounting(cache);
        }

        [Fact]
        public async Task Load_ConcurrentReadersOfOneKey_LoseNoFrame()
        {
            using var cache = CreateCache();
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            var factoryCalls = 0;

            PageBuffer Load()
            {
                return cache.GetReadablePage(PAGE_SIZE, FileOrigin.Data, (_, page) =>
                {
                    Interlocked.Increment(ref factoryCalls);
                    entered.Set();
                    resume.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                    page.Write(42, 0);
                });
            }

            var firstTask = Task.Run(Load);
            entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            var secondTask = Task.Run(Load);

            resume.Set();
            var pages = await Task.WhenAll(firstTask, secondTask);

            factoryCalls.Should().Be(1);
            pages[0].Should().BeSameAs(pages[1]);
            pages[0].ShareCounter.Should().Be(2);

            pages[0].Release();
            pages[1].Release();
            AssertAccounting(cache);
        }

        [Fact]
        public async Task Load_FactoryThrows_ReturnsFrameAndUnblocksWaiters()
        {
            using var cache = CreateCache();
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            var calls = 0;

            PageBuffer Load()
            {
                return cache.GetReadablePage(PAGE_SIZE, FileOrigin.Data, (_, page) =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        entered.Set();
                        resume.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                        throw new IOException("injected read failure");
                    }

                    page.Write(7, 0);
                });
            }

            var failing = Task.Run(() => Record.Exception(Load));
            entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            var waiter = Task.Run(Load);
            resume.Set();

            (await failing).Should().BeOfType<IOException>();
            var loaded = await waiter;

            calls.Should().Be(2);
            loaded.ReadInt32(0).Should().Be(7);
            loaded.Release();
            cache.LoadingPages.Should().Be(0);
            AssertAccounting(cache);
        }

        [Fact]
        public async Task Load_WaiterWakesAfterFrameReused_ReResolvesKey()
        {
            using var cache = CreateCache();
            using var loaderEntered = new ManualResetEventSlim();
            using var waiterWaiting = new ManualResetEventSlim();
            using var failLoader = new ManualResetEventSlim();
            using var waiterAwakened = new ManualResetEventSlim();
            var calls = 0;
            var paused = 0;
            byte[] failedFrameArray = null;
            var failedFrameOffset = -1;

            cache.LoadingWaiterWaiting = waiterWaiting.Set;
            cache.LoadingWaiterResuming = waitAgain =>
            {
                if (Interlocked.Exchange(ref paused, 1) == 0)
                {
                    waiterAwakened.Set();
                    waitAgain();
                }
            };

            PageBuffer LoadContendedKey()
            {
                return cache.GetReadablePage(PAGE_SIZE, FileOrigin.Data, (_, page) =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        failedFrameArray = page.Array;
                        failedFrameOffset = page.Offset;
                        loaderEntered.Set();
                        failLoader.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                        throw new IOException("injected read failure");
                    }

                    page.Write(11, 0);
                });
            }

            var failing = Task.Run(() => Record.Exception(LoadContendedKey));
            loaderEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            var waiter = Task.Run(LoadContendedKey);
            waiterWaiting.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

            failLoader.Set();
            waiterAwakened.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

            var reused = await Task.Run(() => Load(cache, 2));
            reused.Array.Should().BeSameAs(failedFrameArray);
            reused.Offset.Should().Be(failedFrameOffset);
            reused.Release();

            (await failing).Should().BeOfType<IOException>();
            var resolved = await waiter;
            resolved.Position.Should().Be(PAGE_SIZE);
            resolved.ReadInt32(0).Should().Be(11);
            resolved.Release();

            cache.LoadingPages.Should().Be(0);
            AssertAccounting(cache);
        }

        [Fact]
        public async Task Pin_AfterLookup_ExcludesEvictionAndReuse()
        {
            using var cache = CreateCache();
            Load(cache, 1).Release();
            using var hitResolved = new ManualResetEventSlim();
            using var finishPin = new ManualResetEventSlim();

            cache.ReadableHitUnderLock = () =>
            {
                hitResolved.Set();
                finishPin.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            };

            var hit = Task.Run(() => Load(cache, 1));
            hitResolved.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            var competitor = Task.Run(() => Load(cache, 2));

            await Task.Delay(100);
            competitor.IsCompleted.Should().BeFalse("the readable lookup and pin share the cache lock");
            finishPin.Set();

            var original = await hit;
            var other = await competitor;
            original.Position.Should().Be(PAGE_SIZE);
            original.ReadInt32(0).Should().Be(1);
            other.Position.Should().Be(2L * PAGE_SIZE);

            original.Release();
            other.Release();
            AssertAccounting(cache);
        }

        [Fact]
        public async Task WritableCopy_SourceCannotChangeDuringCopy()
        {
            using var cache = CreateCache();
            var source = Load(cache, 1);
            using var copyStarted = new ManualResetEventSlim();
            using var finishCopy = new ManualResetEventSlim();

            cache.WritableCopyUnderLock = () =>
            {
                copyStarted.Set();
                finishCopy.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            };

            var copying = Task.Run(() => cache.GetWritablePage(
                PAGE_SIZE,
                FileOrigin.Data,
                (_, __) => throw new InvalidOperationException("cached source must be copied")));
            copyStarted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            var competitor = Task.Run(() => Load(cache, 2));

            await Task.Delay(100);
            competitor.IsCompleted.Should().BeFalse("copying is performed while eviction is excluded");
            finishCopy.Set();

            var writable = await copying;
            var other = await competitor;
            writable.ReadInt32(0).Should().Be(1);

            source.Release();
            cache.DiscardPage(writable);
            other.Release();
            AssertAccounting(cache);
        }

        [Fact]
        public void WritableLoad_FactoryThrows_ReturnsFrame()
        {
            using var cache = CreateCache();

            Action load = () => cache.GetWritablePage(
                PAGE_SIZE,
                FileOrigin.Data,
                (_, __) => throw new IOException("injected writable read failure"));

            load.Should().Throw<IOException>();
            cache.WritablePages.Should().Be(0);
            cache.LoadingPages.Should().Be(0);
            cache.FreePages.Should().Be(cache.TotalPages);
            AssertAccounting(cache);
        }

        [Fact]
        public void WritableCopy_TestHookThrows_ReturnsFrameAndSourcePin()
        {
            using var cache = CreateCache();
            Load(cache, 1).Release();
            cache.WritableCopyUnderLock = () => throw new IOException("copy failed");

            Action copy = () => cache.GetWritablePage(
                PAGE_SIZE,
                FileOrigin.Data,
                (_, __) => throw new InvalidOperationException("cached source must be copied"));

            copy.Should().Throw<IOException>().WithMessage("copy failed");
            cache.WritablePages.Should().Be(0);
            cache.PinnedPages.Should().Be(0);
            AssertAccounting(cache);
        }

        [Fact]
        public void WritableCopy_FullCache_DoesNotEvictReadableSource()
        {
            using var cache = CreateCache();

            for (var i = 0; i < cache.LimitPagesRounded; i++)
            {
                Load(cache, i).Release();
            }

            var factoryCalls = 0;
            var writable = cache.GetWritablePage(
                PAGE_SIZE,
                FileOrigin.Data,
                (_, __) => Interlocked.Increment(ref factoryCalls));

            factoryCalls.Should().Be(0);
            writable.ReadInt32(0).Should().Be(1);
            cache.DiscardPage(writable);
            AssertAccounting(cache);
        }

        [Fact]
        public void Cache_AllIdleAllReferenced_EvictsOnSecondPass()
        {
            using var cache = CreateCache(evictBudget: 2);

            for (var i = 0; i < cache.LimitPagesRounded; i++)
            {
                var page = Load(cache, i);
                page.Release();
            }

            var segments = cache.Segments;
            var replacement = Load(cache, 1000);
            replacement.Release();

            cache.Segments.Should().Be(segments);
            cache.TotalPages.Should().Be(cache.LimitPagesRounded);
            cache.EvictedPages.Should().BeGreaterThan(0);
            cache.BudgetExceeded.Should().Be(1);
            AssertAccounting(cache);
        }

        [Fact]
        [Trait("Category", "Memory")]
        public void Acquire_BudgetExhausted_At64MiB_EvictsBeforeAllocating()
        {
            using var cache = new MemoryCache(MEMORY_SEGMENT_SIZES, DEFAULT_CACHE_SIZE, 256);

            for (var i = 0; i < cache.LimitPagesRounded; i++)
            {
                Load(cache, i).Release();
            }

            cache.LimitPagesRounded.Should().Be(8200);
            var segments = cache.Segments;
            var replacement = Load(cache, cache.LimitPagesRounded + 1);
            replacement.Release();

            cache.Segments.Should().Be(segments);
            cache.TotalPages.Should().Be(cache.LimitPagesRounded);
            cache.BudgetExceeded.Should().Be(1);
            cache.FramesExamined.Should().BeGreaterThan(256);
            cache.FramesExamined.Should().BeLessThanOrEqualTo(cache.TotalPages * 2L);
            cache.LostFrames.Should().Be(0);
            AssertAccounting(cache);
        }

        [Fact]
        public void Trim_AllPinned_TerminatesWithoutProgress()
        {
            using var cache = CreateCache();
            var pinned = Enumerable.Range(0, cache.LimitPagesRounded + 1)
                .Select(i => Load(cache, i))
                .ToList();

            cache.TotalPages.Should().BeGreaterThan(cache.LimitPagesRounded);
            cache.OverflowSegments.Should().BeGreaterThan(0);

            cache.TrimToLimit();

            cache.TotalPages.Should().BeGreaterThan(cache.LimitPagesRounded);
            cache.PinnedPages.Should().Be(pinned.Count);

            foreach (var page in pinned) page.Release();
            AssertAccounting(cache);
        }

        [Fact]
        public void Trim_ReleasesSegments_AfterPeak()
        {
            using var cache = CreateCache();
            var pages = Enumerable.Range(0, cache.LimitPagesRounded * 4)
                .Select(i => Load(cache, i))
                .ToList();

            foreach (var page in pages) page.Release();
            pages.Clear();

            cache.TrimToLimit();

            cache.TotalPages.Should().BeLessThanOrEqualTo(cache.LimitPagesRounded);
            cache.ReleasedSegments.Should().BeGreaterThan(0);
            AssertAccounting(cache);
        }

        [Fact]
        public void Trim_ScatteredPins_ReportsRetention()
        {
            using var cache = CreateCache();
            var pages = Enumerable.Range(0, cache.LimitPagesRounded + 1)
                .Select(i => Load(cache, i))
                .ToList();
            var retainedPins = new HashSet<PageBuffer>(pages.GroupBy(x => x.Segment).Select(x => x.First()));

            foreach (var page in pages.Where(x => !retainedPins.Contains(x))) page.Release();

            cache.TrimToLimit();

            cache.ReleasedSegments.Should().Be(0);
            cache.RetainedBySegments.Should().Be(cache.TotalPages - retainedPins.Count);

            foreach (var page in retainedPins) page.Release();
            AssertAccounting(cache);
        }

        [Fact]
        public void Pin_ConcurrentIncrementDecrement_NeverLosesADecrement()
        {
            using var cache = CreateCache();
            Load(cache, 1).Release();
            var errors = new ConcurrentQueue<Exception>();

            Parallel.For(0, 16, _ =>
            {
                try
                {
                    for (var i = 0; i < 1000; i++)
                    {
                        var page = Load(cache, 1);
                        page.Release();
                    }
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            });

            errors.Should().BeEmpty();
            cache.PinnedPages.Should().Be(0);
            cache.GetPages().Single().ShareCounter.Should().Be(0);
            AssertAccounting(cache);
        }

        [Fact]
        public void MoveToReadable_KeyCollision_FailsInvariant()
        {
            using var cache = CreateCache();
            var existing = Load(cache, 1);
            existing.Release();
            var writable = cache.NewPage();
            writable.Position = PAGE_SIZE;
            writable.Origin = FileOrigin.Data;

            Action publish = () => cache.MoveToReadable(writable);

            publish.Should().Throw<LiteException>();
            writable.State.Should().Be(FrameState.Writable);
            cache.GetPages().Should().ContainSingle().Which.Should().BeSameAs(existing);

            cache.DiscardPage(writable);
            AssertAccounting(cache);
        }

        [Fact]
        public void FreedSlice_FailsGenerationCheck_AndFrameIsPoisoned()
        {
            using var cache = CreateCache();
            var page = cache.NewPage();
            page.Write(123, 0);
            var slice = page.Slice(0, sizeof(int));

            cache.DiscardPage(page);

            page.Array.Skip(page.Offset).Take(page.Count).Should().OnlyContain(x => x == 0xFF);
            Action readStaleSlice = () => slice.ReadInt32(0);
            readStaleSlice.Should().Throw<LiteException>();
        }

        [Fact]
        public void RecycledFrame_FailsBasePageGenerationCheck()
        {
            using var cache = CreateCache();
            var frame = cache.NewPage();
            var stalePage = new BasePage(frame, 1, PageType.Data);

            cache.DiscardPage(frame);
            var replacement = cache.NewPage();
            replacement.Should().BeSameAs(frame);

            Action accessStalePage = () => _ = stalePage.Buffer;
            accessStalePage.Should().Throw<LiteException>()
                .WithMessage("*page belongs to a recycled cache frame*");

            cache.DiscardPage(replacement);
        }

        [Fact]
        public void Invalidate_WithinLimit_ReleasesNoSegment()
        {
            using var cache = CreateCache();

            for (var i = 0; i < cache.LimitPagesRounded; i++)
            {
                Load(cache, i).Release();
            }

            var segments = cache.Segments;
            cache.Invalidate().Should().Be(cache.LimitPagesRounded);

            cache.Segments.Should().Be(segments);
            cache.ReleasedSegments.Should().Be(0);
            cache.ReadablePages.Should().Be(0);
            cache.FreePages.Should().Be(cache.TotalPages);
            AssertAccounting(cache);
        }

        [Fact]
        public void Invalidate_AfterCheckpoint_ReadsNewContent()
        {
            using var cache = CreateCache();
            var persistedValue = 1;

            PageBuffer Read()
            {
                return cache.GetReadablePage(PAGE_SIZE, FileOrigin.Data,
                    (_, page) => page.Write(persistedValue, 0));
            }

            var before = Read();
            before.ReadInt32(0).Should().Be(1);
            before.Release();

            persistedValue = 2;
            var segments = cache.Segments;
            cache.Invalidate().Should().Be(1);

            var after = Read();
            after.ReadInt32(0).Should().Be(2);
            after.Release();
            cache.Segments.Should().Be(segments);
            AssertAccounting(cache);
        }

        [Fact]
        public void Cache_UniqueIDNumbering_IsMonotonic()
        {
            using var cache = CreateCache();
            var pages = new List<PageBuffer>();

            for (var i = 1; i <= 12; i++)
            {
                var page = cache.NewPage();
                page.UniqueID.Should().Be(i);
                pages.Add(page);
            }

            foreach (var page in pages) cache.DiscardPage(page);
            AssertAccounting(cache);
        }

        [Fact]
        public void Release_LastPin_MovesSegmentToReleasableState()
        {
            using var cache = CreateCache();
            var page = Load(cache, 1);

            page.Segment.Busy.Should().Be(1);
            page.Release();

            page.Segment.Busy.Should().Be(0);
            cache.PinnedPages.Should().Be(0);
            cache.IdleReadablePages.Should().Be(1);
            AssertAccounting(cache);
        }

        [Fact]
        public async Task Release_ConcurrentRepin_KeepsSegmentBusy()
        {
            using var cache = CreateCache();
            var first = Load(cache, 1);
            PageBuffer second = null;
            using var start = new ManualResetEventSlim();

            var repin = Task.Run(() =>
            {
                start.Wait();
                second = Load(cache, 1);
            });

            start.Set();
            first.Release();
            var completed = await Task.WhenAny(repin, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.Should().Be(repin);
            await repin;

            second.Should().NotBeNull();
            second.Segment.Busy.Should().Be(1);
            cache.TrimToLimit();
            second.State.Should().Be(FrameState.Readable);

            second.Release();
            AssertAccounting(cache);
        }

        [Fact]
        public void Cache_AllocatesFromMostPopulatedSegment()
        {
            using var cache = new MemoryCache(new[] { 2 }, PAGE_SIZE * 6L);
            var first = cache.NewPage();
            var second = cache.NewPage();
            var third = cache.NewPage();
            var fourth = cache.NewPage();

            cache.DiscardPage(second);
            cache.DiscardPage(fourth);

            var replacement1 = cache.NewPage();
            var replacement2 = cache.NewPage();

            replacement1.Segment.Should().BeSameAs(first.Segment);
            replacement2.Segment.Should().BeSameAs(third.Segment);

            foreach (var page in new[] { first, third, replacement1, replacement2 }) cache.DiscardPage(page);
            AssertAccounting(cache);
        }

        [Fact]
        public void Cache_ReferencedBit_ProtectsHotPage()
        {
            using var cache = CreateCache();

            for (var i = 0; i < cache.LimitPagesRounded; i++) Load(cache, i).Release();

            foreach (var page in cache.GetPages()) page.Referenced = 0;
            var hot = cache.GetPages().Single(x => x.ReadInt32(0) == 0);
            hot.Referenced = 1;

            Load(cache, 100).Release();

            cache.GetPages().Should().Contain(hot);
            hot.State.Should().Be(FrameState.Readable);
            AssertAccounting(cache);
        }

        [Fact]
        public void Trim_PrefersReleasableSegments()
        {
            using var cache = CreateCache();
            var pages = Enumerable.Range(0, cache.LimitPagesRounded * 2)
                .Select(i => Load(cache, i))
                .ToList();
            var segments = pages.Select(x => x.Segment).Distinct().ToList();

            // Keep one pin in the first and last segments. Release every page
            // in two middle segments so trim can drop one and retain one spare.
            var retained = new HashSet<PageBuffer>
            {
                pages.First(x => ReferenceEquals(x.Segment, segments.First())),
                pages.First(x => ReferenceEquals(x.Segment, segments.Last()))
            };

            foreach (var page in pages.Where(x => !retained.Contains(x))) page.Release();

            cache.TrimToLimit();

            cache.ReleasedSegments.Should().BeGreaterThan(0);
            retained.Should().OnlyContain(x => x.State == FrameState.Readable);

            foreach (var page in retained) page.Release();
            AssertAccounting(cache);
        }

        [Fact]
        public void CacheDispose_DropsSegmentArrays_ForCollection()
        {
            var references = CreateAndDisposeCache();

            ForceFullCollection();

            references.Should().OnlyContain(reference => !reference.IsAlive);
        }

        [Fact]
        public void Trim_ReleasesPeakSegmentArrays_ForCollection()
        {
            var state = CreatePeakAndTrimCache();

            try
            {
                ForceFullCollection();

                state.ReleasedSegments.Should().BeGreaterThan(0);
                state.References.Count(reference => !reference.IsAlive)
                    .Should().BeGreaterThanOrEqualTo((int)state.ReleasedSegments);
                state.Cache.TotalPages.Should().BeLessThanOrEqualTo(state.Cache.LimitPagesRounded);
                AssertAccounting(state.Cache);
            }
            finally
            {
                state.Cache.Dispose();
            }
        }

        [Fact]
        public void Segment_ReleasedButHeldBySuspendedCursor_NotCollectable()
        {
            var state = CreateTrimmedCacheWithSuspendedCursor();

            try
            {
                ForceFullCollection();
                state.Reference.IsAlive.Should().BeTrue("the suspended cursor still references the released segment array");

                DropCursor(state);
                ForceFullCollection();
                state.Reference.IsAlive.Should().BeFalse("dropping the cursor releases the last reference to the segment array");
            }
            finally
            {
                state.Cache.Dispose();
            }
        }

        [Fact]
        public void Cache_DefaultLimits_UseStorageProfileAndSegmentRounding()
        {
            new EngineSettings { Filename = "file.db" }.GetCacheSize()
                .Should().Be(DEFAULT_CACHE_SIZE);
            new EngineSettings { Filename = ":memory:" }.GetCacheSize()
                .Should().Be(MEMORY_CACHE_SIZE);
            new EngineSettings { DataStream = new MemoryStream() }.GetCacheSize()
                .Should().Be(MEMORY_CACHE_SIZE);

            using var cache = new MemoryCache(MEMORY_SEGMENT_SIZES, DEFAULT_CACHE_SIZE);
            cache.TotalPages.Should().Be(MEMORY_SEGMENT_SIZES[0]);
            cache.LimitPagesRounded.Should().Be(8200);

            using var minimum = new MemoryCache(MEMORY_SEGMENT_SIZES, 1);
            minimum.LimitPagesRounded.Should().Be(136);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ICollection<WeakReference> CreateAndDisposeCache()
        {
            var cache = CreateCache();
            var references = cache.GetSegmentWeakReferences();
            cache.Dispose();
            return references;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static CacheCollectionState CreatePeakAndTrimCache()
        {
            var cache = CreateCache();
            var pages = Enumerable.Range(0, cache.LimitPagesRounded * 4)
                .Select(i => Load(cache, i))
                .ToList();
            var references = cache.GetSegmentWeakReferences();

            foreach (var page in pages) page.Release();
            pages.Clear();
            cache.TrimToLimit();

            return new CacheCollectionState
            {
                Cache = cache,
                References = references,
                ReleasedSegments = cache.ReleasedSegments
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SuspendedCursorState CreateTrimmedCacheWithSuspendedCursor()
        {
            var cache = CreateCache();
            var pinned = Enumerable.Range(0, 7).Select(i => Load(cache, i)).ToList();
            var cursor = CreateSliceCursor(cache).GetEnumerator();
            cursor.MoveNext().Should().BeTrue();
            var releasedArray = cursor.Current.Array;
            var reference = new WeakReference(releasedArray);

            foreach (var page in pinned) page.Release();
            pinned.Clear();
            cache.TrimToLimit();

            cache.ReleasedSegments.Should().BeGreaterThan(0);
            cache.GetSegmentWeakReferences().Select(x => x.Target)
                .Should().NotContain(x => ReferenceEquals(x, releasedArray));

            return new SuspendedCursorState { Cache = cache, Cursor = cursor, Reference = reference };
        }

        private static IEnumerable<BufferSlice> CreateSliceCursor(MemoryCache cache)
        {
            var page = cache.NewPage();
            var slice = page.Slice(0, sizeof(int));
            cache.DiscardPage(page);

            yield return slice;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DropCursor(SuspendedCursorState state)
        {
            state.Cursor.Dispose();
            state.Cursor = null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ForceFullCollection()
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        private static MemoryCache CreateCache(int evictBudget = MemoryCache.DEFAULT_EVICT_SCAN_BUDGET)
        {
            return new MemoryCache(new[] { 2 }, PAGE_SIZE * 4L, evictBudget);
        }

        private static PageBuffer Load(MemoryCache cache, int pageNumber)
        {
            return cache.GetReadablePage(pageNumber * (long)PAGE_SIZE, FileOrigin.Data, (_, page) => page.Write(pageNumber, 0));
        }

        private static void AssertAccounting(MemoryCache cache)
        {
            cache.TotalPages.Should().Be(cache.FreePages + cache.ReadablePages + cache.WritablePages + cache.LoadingPages);
            cache.ReadablePages.Should().Be(cache.IdleReadablePages + cache.PinnedPages);
        }

        private sealed class CacheCollectionState
        {
            public MemoryCache Cache { get; set; }
            public ICollection<WeakReference> References { get; set; }
            public long ReleasedSegments { get; set; }
        }

        private sealed class SuspendedCursorState
        {
            public MemoryCache Cache { get; set; }
            public IEnumerator<BufferSlice> Cursor { get; set; }
            public WeakReference Reference { get; set; }
        }
    }
}
