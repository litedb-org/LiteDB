using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class WalCombinedPowerLoss_Tests
    {
        [Theory]
        [InlineData(512, false)]
        [InlineData(4096, false)]
        [InlineData(512, true)]
        [InlineData(4096, true)]
        public void CombinedWrite_EverySectorCutRecoversOnlyWholeTransactions(int sector, bool durable)
        {
            using var source = new WalCombinedWriteScenario(durable);
            source.Capture();
            var end = source.Start + WalChecksum.FrameSize;
            var cuts = new SortedSet<int> { source.Start, end - 1, end, end + 1, source.After.Length };
            for (var cut = (source.Start / sector + 1) * sector; cut < source.After.Length; cut += sector) cuts.Add(cut);

            foreach (var cut in cuts)
            {
                using var scope = new AssertionScope($"sector={sector}, durable={durable}, cut={cut - source.Start}");
                // Short EOF; full EOF with only the prefix persisted; suffix-first persistence;
                // and a single missing sector despite the later confirmation/padding persisting.
                var prefix = source.After.Take(cut).ToArray();
                var extended = new byte[source.After.Length];
                Buffer.BlockCopy(prefix, 0, extended, 0, prefix.Length);
                var suffix = (byte[])source.After.Clone();
                Array.Clear(suffix, source.Start, cut - source.Start);
                var missing = (byte[])source.After.Clone();
                Array.Clear(missing, cut, Math.Min(sector, missing.Length - cut));
                foreach (var image in new[] { prefix, extended, suffix, missing })
                {
                    // Byte comparison is independent of the product's CRC/recovery implementation.
                    var complete = image.Length >= end && image.Skip(source.Start).Take(WalChecksum.FrameSize)
                        .SequenceEqual(source.After.Skip(source.Start).Take(WalChecksum.FrameSize));
                    source.AssertRecovered(image, complete ? 2 : 1);
                }
            }
            // Positive control: the entire write can survive before its flush/acknowledgement.
            source.AssertRecovered(source.After, 2);
        }

        [Theory]
        [InlineData(512)]
        [InlineData(4096)]
        public void CombinedWrite_ConfirmationAndPaddingCannotHideAMissingEarlierFrame(int sector)
        {
            using var source = new WalCombinedWriteScenario(true);
            var acknowledged = LiteDB.Tests.TempFile.ReadAllBytesShared(source.LogName);
            var firstNewFrame = acknowledged.Length / WalChecksum.FrameSize * WalChecksum.FrameSize;
            source.Capture();
            firstNewFrame.Should().BeLessThan(source.Start);
            for (var position = firstNewFrame; position < source.Start; position += sector)
            {
                using var scope = new AssertionScope($"missing sector at {position}, confirmation at {source.Start}");
                var image = (byte[])source.After.Clone();
                Array.Clear(image, position, Math.Min(sector, source.Start - position));
                source.AssertRecovered(image, image.SequenceEqual(source.After) ? 2 : 1);
            }
        }
    }
}
