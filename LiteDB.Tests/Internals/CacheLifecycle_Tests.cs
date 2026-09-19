using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class CacheLifecycle_Tests
    {
        [Fact]
        public void Dispose_WithPinnedReaders_PreservesPagesUntilReleased()
        {
            using var cache = CreateCache();
            var first = cache.GetReadablePage(0, FileOrigin.Data, (_, page) => page.Write(42, 0));
            var second = cache.GetReadablePage(0, FileOrigin.Data, (_, __) => throw new Exception());

            Action dispose = cache.Dispose;
            dispose.Should().Throw<InvalidOperationException>().WithMessage("*Finish active operations*");
            first.ReadInt32(0).Should().Be(42);
            first.ShareCounter.Should().Be(2);
            first.Release();
            second.Release();

            cache.PinnedPages.Should().Be(0);
            cache.LostFrames.Should().Be(0);
            cache.Dispose();
            cache.TotalPages.Should().Be(0);
        }

        [Fact]
        public void Dispose_WithWritablePage_PreservesOwnershipUntilDiscarded()
        {
            using var cache = CreateCache();
            var page = cache.NewPage();
            page.Write(42, 0);

            Action dispose = cache.Dispose;
            dispose.Should().Throw<InvalidOperationException>().WithMessage("*Finish active operations*");
            page.ReadInt32(0).Should().Be(42);
            cache.DiscardPage(page);

            cache.WritablePages.Should().Be(0);
            cache.LostFrames.Should().Be(0);
            cache.Dispose();
            cache.TotalPages.Should().Be(0);
        }

        [Fact]
        public async Task Dispose_DuringLoad_PreservesFrameUntilPublication()
        {
            using var cache = CreateCache();
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            var loading = Task.Run(() => cache.GetReadablePage(0, FileOrigin.Data, (_, page) =>
            {
                entered.Set();
                resume.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                page.Write(42, 0);
            }));

            try
            {
                entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                Action dispose = cache.Dispose;
                dispose.Should().Throw<InvalidOperationException>().WithMessage("*Finish active operations*");
                cache.LoadingPages.Should().Be(1);
            }
            finally
            {
                resume.Set();
            }

            var loaded = await loading;
            loaded.ReadInt32(0).Should().Be(42);
            loaded.Release();
            cache.LoadingPages.Should().Be(0);
            cache.LostFrames.Should().Be(0);
            cache.Dispose();
            cache.TotalPages.Should().Be(0);
        }

        [Fact]
        public void Trim_WithOnlyInitialSegmentIdle_PreservesItsCachedPages()
        {
            using var cache = CreateCache();
            var pages = Enumerable.Range(0, 6).Select(i => cache.GetReadablePage(
                i * PAGE_SIZE, FileOrigin.Data, (_, page) => page.Write(i, 0))).ToArray();
            pages[0].Release();
            pages[1].Release();
            cache.TotalPages.Should().BeGreaterThan(cache.LimitPagesRounded);

            try
            {
                cache.TrimToLimit();
                cache.EvictedPages.Should().Be(0, "the initial segment cannot be released");
                cache.GetReadablePage(0, FileOrigin.Data, (_, __) => throw new Exception("Unexpected reload"))
                    .Release();
                cache.GetReadablePage(PAGE_SIZE, FileOrigin.Data, (_, __) => throw new Exception("Unexpected reload"))
                    .Release();
            }
            finally
            {
                foreach (var page in pages.Skip(2)) page.Release();
            }

            cache.TrimToLimit();
            cache.TotalPages.Should().BeLessThanOrEqualTo(cache.LimitPagesRounded);
            cache.LostFrames.Should().Be(0);
        }

        private static MemoryCache CreateCache() => new MemoryCache(new[] { 2 }, PAGE_SIZE * 4L);
    }
}
