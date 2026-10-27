#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;

namespace LiteDB.Tests.Internals
{
    internal static class GroupCommitRecovery
    {
        internal static void CheckUnsyncedTails(byte[] data, byte[] firstBarrier, byte[] pending, int writers)
        {
            // The first sync's frames survive exactly. Model each later frame
            // torn, lost, or still in volatile cache while subsequent writes may
            // have reached storage. Only writer 1 was acknowledged at this point.
            var firstEnd = firstBarrier.Length / WalChecksum.FrameSize * WalChecksum.FrameSize;
            for (var offset = firstEnd; offset + WalChecksum.FrameSize <= pending.Length; offset += WalChecksum.FrameSize)
            {
                foreach (var kind in new[] { 0, 1, 2 })
                {
                    var image = (byte[])pending.Clone();
                    if (kind == 0) image[offset + 400] ^= 1;
                    else if (kind == 1) Array.Clear(image, offset, WalChecksum.FrameSize);
                    else Array.Resize(ref image, offset + 17);
                    Verify(data, image, writers);
                }
            }
        }

        private static void Verify(byte[] data, byte[] log, int writers)
        {
            using var file = new TempFile();
            var logPath = FileHelper.GetLogFile(file.Filename);
            File.WriteAllBytes(file.Filename, data);
            File.WriteAllBytes(logPath, log);
            int[] expected;
            using (var copy = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true }))
            {
                expected = CheckRows(copy, writers);
            }
            File.ReadAllBytes(file.Filename).Should().Equal(data);
            File.ReadAllBytes(logPath).Should().Equal(log);
            for (var open = 0; open < 2; open++)
            {
                using var copy = new LiteDatabase(file.Filename);
                CheckRows(copy, writers).Should().BeEquivalentTo(expected);
                copy.Checkpoint();
            }
        }

        private static int[] CheckRows(LiteDatabase copy, int writers)
        {
            var rows = copy.GetCollection("rows");
            var all = rows.FindAll().ToArray();
            var ids = all.Select(row => row["_id"].AsInt32).ToArray();
            ids.Should().Contain(new[] { 0, 1 }, "the seed and first acknowledged writer survive every image");
            ids.Should().OnlyHaveUniqueItems().And.OnlyContain(id => id >= 0 && id <= writers);
            all.Should().OnlyContain(row => row["value"].AsInt32 == row["_id"].AsInt32);
            rows.Find(Query.GT("value", 0)).Select(row => row["value"].AsInt32)
                .Should().BeEquivalentTo(ids.Where(id => id > 0));
            return ids;
        }
    }
}
#endif
