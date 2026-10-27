using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class WalCombinedAlignment_Tests
    {
        [Theory]
        [InlineData(512)]
        [InlineData(4096)]
        public void EveryFrameAlignment_RejectsTornFramesAndNeverCountsPaddingAsAFrame(int sector)
        {
            using var file = new TempFile();
            for (var preceding = 0; preceding < 128; preceding++)
            {
                using var scope = new AssertionScope($"sector={sector}, preceding={preceding}");
                using var raw = new FileStream(file.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
                var checksum = new WalChecksum();
                checksum.Reset(new byte[16]);
                using var writer = new ChecksummedWalStream(raw, checksum);
                var page = Page();
                for (var i = 0; i < preceding; i++) writer.Write(page, 0, page.Length);
                var start = preceding * WalChecksum.FrameSize;
                page[BasePage.P_IS_CONFIRMED] = 1;
                writer.WriteFinalPadded(page, 0);
                var end = start + WalChecksum.FrameSize;
                var padding = checked((int)raw.Length - end);
                padding.Should().BeInRange(0, PAGE_SIZE - WalChecksum.MetadataSize);
                (raw.Length - start).Should().BeInRange(WalChecksum.FrameSize, 2 * PAGE_SIZE);
                (raw.Length % PAGE_SIZE).Should().Be(0, "the combined write itself must align EOF before flush");
                writer.Length.Should().Be((preceding + 1L) * PAGE_SIZE);
                writer.TrailingBytes.Should().Be(0);
                raw.Position = start;
                var combined = new byte[checked((int)raw.Length - start)];
                raw.ReadRequired(combined, 0, combined.Length);
                combined.Skip(WalChecksum.FrameSize).Should().Equal(new byte[padding]);
                WalChecksum.IsFrame(new byte[WalChecksum.FrameSize], (preceding + 1L) * PAGE_SIZE).Should().BeFalse();

                for (var boundary = (start / sector + 1) * sector; boundary < raw.Length; boundary += sector)
                {
                    var cut = boundary - start;
                    // A full logical EOF may survive with either side of the physical write lost.
                    foreach (var keepPrefix in new[] { false, true })
                    {
                        var torn = new byte[WalChecksum.FrameSize];
                        if (keepPrefix) Buffer.BlockCopy(combined, 0, torn, 0, Math.Min(cut, torn.Length));
                        else if (cut < torn.Length) Buffer.BlockCopy(combined, cut, torn, cut, torn.Length - cut);
                        var intact = torn.SequenceEqual(combined.Take(torn.Length));
                        WalChecksum.IsFrame(torn, preceding * (long)PAGE_SIZE).Should().Be(intact,
                            $"boundary {boundary}, keepPrefix {keepPrefix}: padding cannot certify a torn frame");
                    }
                }
            }
        }

        private static byte[] Page()
        {
            var page = Enumerable.Repeat((byte)0x5a, PAGE_SIZE).ToArray();
            var slice = new BufferSlice(page, 0, PAGE_SIZE);
            slice.Write(1u, BasePage.P_PAGE_ID);
            slice.Write(1u, BasePage.P_TRANSACTION_ID);
            slice[BasePage.P_PAGE_TYPE] = (byte)PageType.Data;
            slice[BasePage.P_IS_CONFIRMED] = 0;
            return page;
        }
    }
}
