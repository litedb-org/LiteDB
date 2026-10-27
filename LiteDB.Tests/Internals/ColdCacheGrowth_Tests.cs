using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Internals
{
    public class ColdCacheGrowth_Tests
    {
        [Theory]
        [InlineData(9)]
        [InlineData(24)]
        public async Task Small_cold_reads_use_a_small_cache_and_keep_concurrent_pins_independent(int count)
        {
            using var cache = new MemoryCache(MEMORY_SEGMENT_SIZES, 64L * 1024 * 1024);
            var pages = await Task.WhenAll(Enumerable.Range(0, count).Select(index => Task.Run(() =>
                cache.GetReadablePage(index * (long)PAGE_SIZE, FileOrigin.Data,
                    (position, bytes) => bytes.Write(index + 1, 0)))));
            try
            {
                cache.AllocatedBytes.Should().BeLessThanOrEqualTo(32L * PAGE_SIZE,
                    "a small cold read should not reserve a megabyte after its eighth page");
                cache.PinnedPages.Should().Be(count);
                for (var index = 0; index < count; index++) pages[index].ReadInt32(0).Should().Be(index + 1);
                cache.LostFrames.Should().Be(0);
            }
            finally { foreach (var page in pages) page.Release(); }
            cache.PinnedPages.Should().Be(0);
            cache.Clear().Should().Be(count);
            cache.LostFrames.Should().Be(0);
        }
    }
}
