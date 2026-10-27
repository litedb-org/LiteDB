using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class WalPadding_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("padding-secret")]
        public void FlushedFrames_SurviveReleasedEngineLengthRoundingAcrossAlignmentRollover(string password)
        {
            using var bytes = new MemoryStream();
            using var factory = new StreamFactory(bytes, password);
            var checksum = new WalChecksum();
            checksum.Reset(new byte[16]);
            using var writer = new ChecksummedWalStream(factory.GetStream(true, false), checksum);
            for (var count = 1; count <= 257; count++)
            {
                var expected = Page(count);
                writer.Write(expected, 0, expected.Length);
                writer.Flush();

                // 128 frames align exactly; the next frame needs almost 8 KiB
                // again. Released 5.0.21 truncates to this boundary on open.
                var before = bytes.ToArray();
                bytes.SetLength(bytes.Length / 8192 * 8192);
                bytes.ToArray().Should().Equal(before);
                var preamble = password == null ? 0 : 8192;
                var physicalFrames = count * 8256L;
                (bytes.Length - preamble).Should().Be((physicalFrames + 8191) / 8192 * 8192);
                writer.Length.Should().Be(count * 8192L);
                writer.TrailingBytes.Should().Be(0);

                if (count != 1 && count != 127 && count != 128 && count != 129 && count != 256 && count != 257) continue;
                using var copy = ChecksumTestFiles.Copy(before);
                using var copyFactory = new StreamFactory(copy, password);
                using var reader = new ChecksummedWalStream(copyFactory.GetStream(false, false), checksum);
                for (var id = 1; id <= count; id++)
                {
                    var actual = new byte[8192];
                    reader.Read(actual, 0, actual.Length).Should().Be(actual.Length);
                    actual.Should().Equal(Page(id));
                }
                reader.Length.Should().Be(count * 8192L);
                copy.ToArray().Should().Equal(before);
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(129)]
        public void FinalFramePaddingWrite_IsByteIdenticalToTheExistingPath(int frames)
        {
            byte[] Write(bool coalesced)
            {
                using var file = new TempFile();
                using (var raw = new FileStream(file.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
                {
                    var checksum = new WalChecksum();
                    checksum.Reset(new byte[16]);
                    using var writer = new ChecksummedWalStream(raw, checksum);
                    for (var id = 1; id < frames; id++)
                    {
                        var page = Page(id);
                        page[BasePage.P_IS_CONFIRMED] = 0;
                        writer.Write(page, 0, page.Length);
                    }
                    var final = Page(frames);
                    if (coalesced) writer.WriteFinalPadded(final, 0);
                    else writer.Write(final, 0, final.Length);
                    writer.Flush();
                }
                return File.ReadAllBytes(file.Filename);
            }

            Write(true).Should().Equal(Write(false));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("padding-secret")]
        public void FinalFramePaddingWrite_FallsBackByteIdenticallyForCallerAndEncryptedStreams(string password)
        {
            byte[] prefix;
            using (var source = new MemoryStream())
            using (var factory = new StreamFactory(source, password))
            {
                var checksum = new WalChecksum();
                checksum.Reset(new byte[16]);
                using var writer = new ChecksummedWalStream(factory.GetStream(true, false), checksum);
                var first = Page(1);
                first[BasePage.P_IS_CONFIRMED] = 0;
                writer.Write(first, 0, first.Length);
                writer.Flush();
                prefix = source.ToArray();
            }

            byte[] Write(bool throughCandidate)
            {
                using var bytes = new MemoryStream();
                bytes.Write(prefix, 0, prefix.Length);
                bytes.Position = 0;
                using var factory = new StreamFactory(bytes, password);
                var checksum = new WalChecksum();
                checksum.Reset(new byte[16]);
                using var writer = new ChecksummedWalStream(factory.GetStream(true, false), checksum);
                var final = Page(2);
                if (throughCandidate) writer.WriteFinalPadded(final, 0);
                else writer.Write(final, 0, final.Length);
                writer.Flush();
                return bytes.ToArray();
            }

            Write(true).Should().Equal(Write(false));
        }

        private static byte[] Page(int id)
        {
            var bytes = new byte[8192];
            var page = new BufferSlice(bytes, 0, bytes.Length);
            page.Write((uint)id, BasePage.P_PAGE_ID);
            page[BasePage.P_PAGE_TYPE] = (byte)PageType.Data;
            page.Write((uint)id, BasePage.P_TRANSACTION_ID);
            page[BasePage.P_IS_CONFIRMED] = 1;
            page[BasePage.P_PAGE_FORMAT] = PageChecksum.Checksummed;
            for (var i = 32; i < bytes.Length; i++) bytes[i] = (byte)(i + id);
            return bytes;
        }
    }
}
