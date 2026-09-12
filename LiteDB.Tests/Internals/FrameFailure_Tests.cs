using System;
using System.IO;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class FrameFailure_Tests
    {
        [Fact]
        public void WritableCopy_TestHookThrows_ReturnsFrameAndSourcePin()
        {
            using var cache = new MemoryCache(new[] { 2 }, PAGE_SIZE * 4L);
            cache.GetReadablePage(PAGE_SIZE, FileOrigin.Data, (_, buffer) => buffer.Write(1, 0)).Release();
            cache.BeforeWritableCopy = () => throw new IOException("copy failed");

            Action copy = () => cache.GetWritablePage(
                PAGE_SIZE,
                FileOrigin.Data,
                (_, __) => throw new InvalidOperationException("cached source must be copied"));

            copy.Should().Throw<IOException>().WithMessage("copy failed");
            cache.WritablePages.Should().Be(0);
            cache.PinnedPages.Should().Be(0);
            cache.LostFrames.Should().Be(0);
        }

        [Fact]
        public void WritableCopy_FullCache_DoesNotEvictReadableSource()
        {
            using var cache = new MemoryCache(new[] { 2 }, PAGE_SIZE * 4L);

            for (var i = 0; i < cache.LimitPagesRounded; i++)
            {
                cache.GetReadablePage(i * PAGE_SIZE, FileOrigin.Data, (_, buffer) => buffer.Write(i, 0)).Release();
            }

            var factoryCalls = 0;
            var writable = cache.GetWritablePage(
                PAGE_SIZE,
                FileOrigin.Data,
                (_, __) => Interlocked.Increment(ref factoryCalls));

            factoryCalls.Should().Be(0);
            writable.ReadInt32(0).Should().Be(1);
            cache.DiscardPage(writable);
            cache.LostFrames.Should().Be(0);
        }

        [Fact]
        public void WritableCopy_Failure_ReturnsDestinationAndUnpinsSource()
        {
            using var cache = new MemoryCache(new[] { 2 }, PAGE_SIZE * 2L);
            var source = cache.GetReadablePage(0, FileOrigin.Data, (_, buffer) => buffer.Write(42, 0));
            source.Release();
            cache.BeforeWritableCopy = () => throw new IOException("copy failed");

            for (var attempt = 0; attempt < 20; attempt++)
            {
                Action copy = () => cache.GetWritablePage(0, FileOrigin.Data, (_, __) => { });
                copy.Should().Throw<IOException>().WithMessage("copy failed");
                cache.WritablePages.Should().Be(0);
                cache.PinnedPages.Should().Be(0);
                cache.FreePages.Should().Be(1);
                cache.LostFrames.Should().Be(0);
            }
        }

        [Fact]
        public void WritableCopy_FullCache_KeepsSourceUntilCopied()
        {
            using var cache = new MemoryCache(new[] { 1 }, PAGE_SIZE);
            var source = cache.GetReadablePage(0, FileOrigin.Data, (_, buffer) => buffer.Write(42, 0));
            source.Release();

            var pinned = cache.GetReadablePage(PAGE_SIZE, FileOrigin.Data, (_, buffer) => buffer.Write(43, 0));

            var copy = cache.GetWritablePage(0, FileOrigin.Data,
                (_, __) => throw new IOException("cached source was evicted"));

            copy.ReadInt32(0).Should().Be(42);
            cache.PinnedPages.Should().Be(1);
            pinned.Release();
            cache.DiscardPage(copy);
            cache.TrimToLimit();
            cache.WritablePages.Should().Be(0);
            cache.TotalPages.Should().Be(cache.LimitPagesRounded);
        }

        [Fact]
        public void LogPublication_Failure_DiscardsUnpublishedFrame()
        {
            using var disk = CreateDisk(out _);
            var existing = disk.Cache.GetReadablePage(0, FileOrigin.Log, (_, buffer) => buffer.Write(42, 0));
            existing.Release();
            var page = disk.NewPage();

            Action write = () => disk.WriteLogDisk(new[] { page });

            write.Should().Throw<LiteException>();
            disk.Cache.WritablePages.Should().Be(0);
            disk.Cache.PinnedPages.Should().Be(0);
            disk.Cache.ReadablePages.Should().Be(1);
            disk.Cache.LostFrames.Should().Be(0);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ReadHook_Failure_ReleasesAcquiredFrame(bool writable)
        {
            using var disk = CreateDisk(out var state);
            using var reader = disk.GetReader();
            state.SimulateDiskReadFail = _ => throw new IOException("read failed");

            for (var attempt = 0; attempt < 20; attempt++)
            {
                Action read = () => reader.ReadPage(0, writable, FileOrigin.Data);
                read.Should().Throw<IOException>().WithMessage("read failed");
                disk.Cache.WritablePages.Should().Be(0);
                disk.Cache.PinnedPages.Should().Be(0);
                disk.Cache.LoadingPages.Should().Be(0);
                disk.Cache.LostFrames.Should().Be(0);
            }
        }

        private static DiskService CreateDisk(out EngineState state)
        {
            var settings = new EngineSettings
            {
                DataStream = new MemoryStream(),
                LogStream = new MemoryStream(),
                CacheSize = PAGE_SIZE * 4L
            };
            state = new EngineState(null, settings);
            return new DiskService(settings, state, new[] { 2 });
        }
    }
}
