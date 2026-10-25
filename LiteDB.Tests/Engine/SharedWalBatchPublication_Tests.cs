using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Engine
{
    public class SharedWalBatchPublication_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void Every_batch_announces_before_overwrite_and_only_exclusive_installers_coalesce(
            bool batch, bool fail)
        {
            using var file = new TempFile();
            var signals = batch ? new BatchedSignals() : new Signals();
            var settings = new EngineSettings { Filename = file, CoordinationSignals = signals };
            var state = new EngineState(null, settings);
            using (var disk = new DiskService(settings, state, new[] { 10 }))
            {
                var positions = new Dictionary<uint, PagePosition>();
                PageBuffer Page(uint id, int value)
                {
                    var page = disk.NewPage();
                    page.Write(id, BasePage.P_PAGE_ID);
                    page.Write(value, PAGE_SIZE - sizeof(int));
                    return page;
                }
                void Write(int value)
                {
                    // Yield lazily, transferring ownership only when the disk consumes it.
                    IEnumerable<PageBuffer> Pages()
                    {
                        yield return Page(1, value);
                        yield return Page(2, value);
                    }
                    disk.WriteLogDisk(Pages(), (id, position) =>
                        positions[id] = new PagePosition(id, position), positions);
                }
                Write(10);
                signals.Reuses.Should().Be(0, "an append-only batch needs no reuse notification");
                var writes = 0;
                state.SimulateDiskWriteFail = page =>
                {
                    writes++;
                    signals.Reuses.Should().Be(batch ? 1 : writes,
                        "publication must precede the first overwrite, including a failed one");
                    if (fail && writes == 2) throw new IOException("injected overwrite failure");
                };
                Action overwrite = () => Write(20);
                if (fail) overwrite.Should().Throw<IOException>().WithMessage("injected overwrite failure");
                else overwrite();
                writes.Should().Be(2);
                state.SimulateDiskWriteFail = null;
                var previous = signals.Reuses;
                Write(30);
                signals.Reuses.Should().Be(previous + (batch ? 1 : 2),
                    "the next batch requires a fresh notification even after failure");
                using var reader = disk.GetReader();
                foreach (var position in positions.Values)
                {
                    var page = reader.ReadPage(position.Position, false, FileOrigin.Log);
                    try { page.ReadInt32(PAGE_SIZE - sizeof(int)).Should().Be(30); }
                    finally { page.Release(); }
                }
                disk.Cache.PinnedPages.Should().Be(0);
                disk.Cache.WritablePages.Should().Be(0);
            }
            // These synthetic frames were never confirmed and cannot become records on reopen.
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollectionNames().Should().BeEmpty();
        }

        private class Signals : ICoordinationSignals
        {
            internal int Reuses;
            public void StructuralBegin() { }
            public void StructuralEnd(int version) { }
            public void SlotReused() { Reuses++; }
            public void Committed(int version) { }
        }

        private sealed class BatchedSignals : Signals, IBatchedCoordinationSignals { }
    }
}
