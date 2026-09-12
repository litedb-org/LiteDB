using System;
using System.IO;
using System.Collections.Generic;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using System.Threading.Tasks;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class Disk_Tests
    {
        [Fact]
        public void Disk_Read_Write()
        {
            var settings = new EngineSettings
            {
                DataStream = new MemoryStream(),
                LogStream = new MemoryStream()
            };

            var state = new EngineState(null, settings);
            var disk = new DiskService(settings, state, new int[] { 10 });
            var pages = new List<PageBuffer>();

            // let's create 100 pages with 0-99 full data
            for (var i = 0; i < 100; i++)
            {
                var p = disk.NewPage();

                p.Fill((byte) i); // fills with 0 - 99

                pages.Add(p);
            }

            // page will be saved in LOG file in PagePosition order (0-99)
            disk.WriteLogDisk(pages);

            // after release, no page can be read/write
            pages.Clear();

            // lets do some read tests
            var reader = disk.GetReader();

            for (var i = 0; i < 100; i++)
            {
                var p = reader.ReadPage(i * 8192, false, FileOrigin.Log);

                p.All((byte) i).Should().BeTrue();

                p.Release();
            }

            // test cache in use
            disk.Cache.PagesInUse.Should().Be(0);

            // wait all async threads
            disk.Dispose();
        }

        [Fact]
        public void WriteLogDisk_PositionsRecordedBeforeRelease()
        {
            using var disk = CreateDisk(out _);
            var page = disk.NewPage();
            page.Write((uint)42, BasePage.P_PAGE_ID);

            disk.WriteLogDisk(new[] { page }, (pageID, position) =>
            {
                pageID.Should().Be(42);
                position.Should().Be(page.Position);
                page.State.Should().Be(FrameState.Readable);
                page.ShareCounter.Should().Be(1);
            });

            page.ShareCounter.Should().Be(0);
            disk.Cache.PinnedPages.Should().Be(0);
        }

        [Fact]
        public void WriteLogDisk_CallbackThrows_ReleasesFrame()
        {
            using var disk = CreateDisk(out _);
            var page = disk.NewPage();

            Action write = () => disk.WriteLogDisk(new[] { page }, (_, __) => throw new IOException("callback failed"));

            write.Should().Throw<IOException>().WithMessage("callback failed");
            page.ShareCounter.Should().Be(0);
            disk.Cache.PinnedPages.Should().Be(0);
        }

        [Fact]
        public void WriteLogDisk_WriteThrows_ReleasesFrame()
        {
            using var disk = CreateDisk(out var state);
            var page = disk.NewPage();
            state.SimulateDiskWriteFail = _ => throw new IOException("write failed");

            Action write = () => disk.WriteLogDisk(new[] { page });

            write.Should().Throw<IOException>().WithMessage("write failed");
            page.ShareCounter.Should().Be(0);
            disk.Cache.PinnedPages.Should().Be(0);
        }

        [Fact]
        public void WriteLogDisk_PublicationCollision_DiscardsWritableFrame()
        {
            using var disk = CreateDisk(out _);
            var existing = disk.Cache.GetReadablePage(0, FileOrigin.Log, (_, page) => page.Write(1, 0));
            existing.Release();
            var writable = disk.NewPage();

            Action write = () => disk.WriteLogDisk(new[] { writable });

            write.Should().Throw<LiteException>();
            writable.State.Should().Be(FrameState.Free);
            disk.Cache.WritablePages.Should().Be(0);
            disk.Cache.PinnedPages.Should().Be(0);
        }

        private static DiskService CreateDisk(out EngineState state)
        {
            var settings = new EngineSettings
            {
                DataStream = new MemoryStream(),
                LogStream = new MemoryStream(),
                CacheSize = PAGE_SIZE * 20L
            };

            state = new EngineState(null, settings);
            return new DiskService(settings, state, new[] { 2 });
        }

        [Fact (Skip = "Verificar loop")]
        public Task Disk_ExclusiveScheduler_Write() => Task.Factory.StartNew(Disk_Read_Write,
            CancellationToken.None, TaskCreationOptions.DenyChildAttach,
            new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler);
    }
}
