using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class MemoryCacheWritableOwnership_Tests
    {
        [Fact]
        public void Idle_frame_reuse_preserves_disk_version_and_invalidates_old_slices()
        {
            using var cache = new MemoryCache(new[] { 2 }, PAGE_SIZE * 2L);
            var original = cache.GetReadablePage(0, FileOrigin.Data, (_, page) => page.Write(42, 0));
            var stale = original.Slice(0, 4);
            original.Release();
            var free = cache.FreePages;
            var writer = cache.GetWritablePage(0, FileOrigin.Data, (_, __) => throw new Exception("cached bytes should be reused"));
            try
            {
                writer.Should().BeSameAs(original);
                cache.FreePages.Should().Be(free, "no second frame is needed for an idle cache hit");
                writer.Write(99, 0);
                Action staleRead = () => stale.ReadInt32(0);
                staleRead.Should().Throw<LiteException>();
                var reader = cache.GetReadablePage(0, FileOrigin.Data, (_, page) => page.Write(42, 0));
                try
                {
                    reader.ReadInt32(0).Should().Be(42, "a new reader must load the unmodified disk version");
                    writer.ReadInt32(0).Should().Be(99);
                }
                finally { reader.Release(); }
            }
            finally { cache.DiscardPage(writer); }
            cache.PinnedPages.Should().Be(0);
            cache.WritablePages.Should().Be(0);
            cache.LostFrames.Should().Be(0);
        }

        [Fact]
        public void Pinned_frame_is_copied_and_existing_reader_remains_unchanged()
        {
            using var cache = new MemoryCache(new[] { 2 }, PAGE_SIZE * 2L);
            var reader = cache.GetReadablePage(0, FileOrigin.Data, (_, page) => page.Write(42, 0));
            try
            {
                var writer = cache.GetWritablePage(0, FileOrigin.Data, (_, __) => throw new Exception("cached bytes should be copied"));
                try
                {
                    writer.Should().NotBeSameAs(reader);
                    writer.Write(99, 0);
                    reader.ReadInt32(0).Should().Be(42);
                    reader.ShareCounter.Should().Be(1);
                }
                finally { cache.DiscardPage(writer); }
            }
            finally { reader.Release(); }
            cache.LostFrames.Should().Be(0);
        }

        [Fact]
        public async Task Stale_shared_hint_cannot_pin_a_frame_transferred_to_a_writer()
        {
            using var cache = new MemoryCache(new[] { 2 }, PAGE_SIZE * 2L);
            var original = cache.GetReadablePage(0, FileOrigin.Data, (_, page) => page.Write(42, 0));
            var hints = new SharedPageReads();
            hints.Remember(original);
            using var observed = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            hints.AfterHintRead = () =>
            {
                observed.Set();
                resume.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            };
            var pending = Task.Run(() => hints.TryPin(0, FileOrigin.Data));
            PageBuffer writer = null;
            var released = false;
            try
            {
                observed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                original.Release();
                released = true;
                writer = cache.GetWritablePage(0, FileOrigin.Data, (_, __) => throw new Exception());
                writer.Should().BeSameAs(original);
                writer.Write(99, 0);
                resume.Set();
                (await pending).Should().BeNull();
                writer.ShareCounter.Should().Be(BUFFER_WRITABLE);
                writer.ReadInt32(0).Should().Be(99);
            }
            finally
            {
                resume.Set();
                try { (await pending)?.Release(); }
                finally
                {
                    if (writer != null) cache.DiscardPage(writer);
                    if (!released) original.Release();
                }
            }
            cache.PinnedPages.Should().Be(0);
            cache.LostFrames.Should().Be(0);
        }
    }
}
