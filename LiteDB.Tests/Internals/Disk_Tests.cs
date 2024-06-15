﻿namespace LiteDB.Internals;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

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
        var disk = new DiskService(settings, state, new[] { 10 });
        var pages = new List<PageBuffer>();

        // let's create 100 pages with 0-99 full data
        for (var i = 0; i < 100; i++)
        {
            var p = disk.NewPage();

            p.Fill((byte) i); // fills with 0 - 99

            pages.Add(p);
        }

        // page will be saved in LOG file in PagePosition order (0-99)
        disk.WriteAsync(pages);

        // wait for async queue writes
        disk.Queue.Value.Wait();

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

    [Fact(Skip = "Verificar loop")]
    public Task Disk_ExclusiveScheduler_Write() => Task.Factory.StartNew(
        Disk_Read_Write,
        CancellationToken.None,
        TaskCreationOptions.DenyChildAttach,
        new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler);
}