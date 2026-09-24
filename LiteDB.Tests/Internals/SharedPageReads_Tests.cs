using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class SharedPageReads_Tests
    {
        [Fact]
        public async Task ReusedHint_ValidatesIdentityAfterPin_AndReturnsTemporaryPin()
        {
            using var cache = new MemoryCache(new[] { 1 }, PAGE_SIZE);
            var old = cache.GetReadablePage(0, FileOrigin.Data, (_, page) => page.Write(1, 0));
            var hints = new SharedPageReads();
            hints.Remember(old);
            using var observed = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            hints.AfterHintRead = () =>
            {
                observed.Set();
                resume.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            };
            var reading = Task.Run(() => hints.TryPin(0, FileOrigin.Data));
            observed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            old.Release();
            cache.Invalidate();
            var replacement = cache.GetReadablePage(PAGE_SIZE, FileOrigin.Data, (_, page) => page.Write(2, 0));
            resume.Set();
            replacement.Should().BeSameAs(old, "the frame must actually be reused to exercise the race");
            (await reading).Should().BeNull();
            replacement.ShareCounter.Should().Be(1);
            replacement.ReadInt32(0).Should().Be(2);
            replacement.Release();
            cache.PinnedPages.Should().Be(0);
            cache.LostFrames.Should().Be(0);
        }

        [Fact]
        public void ConcurrentSharing_WithEviction_PreservesBytesAndFrameAccounting()
        {
            using var cache = new MemoryCache(new[] { 2 }, PAGE_SIZE * 4L);
            var owner = cache.GetReadablePage(0, FileOrigin.Data, (_, page) => page.Write(42, 0));
            Parallel.For(0, 16, worker =>
            {
                for (var i = 0; i < 2000; i++)
                {
                    var shared = cache.GetReadablePage(0, FileOrigin.Data, (_, __) => throw new Exception("pinned source was lost"));
                    if (shared.ReadInt32(0) != 42) throw new Exception("shared bytes changed");
                    shared.Release();
                    if (i % 20 == 0)
                    {
                        var position = (worker * 2000L + i + 1) * PAGE_SIZE;
                        cache.GetReadablePage(position, FileOrigin.Data, (_, page) => page.Write(position, 0)).Release();
                    }
                }
            });
            owner.ShareCounter.Should().Be(1);
            owner.Release();
            cache.TrimToLimit();
            cache.PinnedPages.Should().Be(0);
            cache.LostFrames.Should().Be(0);
            cache.GetPages().Should().OnlyContain(page => page.ShareCounter == 0);
            cache.TotalPages.Should().BeLessThanOrEqualTo(cache.LimitPagesRounded);
        }

        [Fact]
        public void HintCollision_DoesNotReturnAnotherPageOrOrigin()
        {
            using var cache = new MemoryCache(new[] { 2 }, PAGE_SIZE * 4L);
            var first = cache.GetReadablePage(0, FileOrigin.Data, (_, page) => page.Write(1, 0));
            var collision = cache.GetReadablePage(4096L * PAGE_SIZE, FileOrigin.Data, (_, page) => page.Write(2, 0));
            var log = cache.GetReadablePage(0, FileOrigin.Log, (_, page) => page.Write(3, 0));
            foreach (var original in new[] { first, collision, log })
            {
                var again = cache.GetReadablePage(original.Position, original.Origin, (_, __) => throw new Exception());
                again.Should().BeSameAs(original);
                again.Release();
                original.Release();
            }
            cache.PinnedPages.Should().Be(0);
            cache.LostFrames.Should().Be(0);
        }
    }
}
