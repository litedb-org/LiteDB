using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    [Trait("Category", "IoSafety")]
    public class ConvertedWalUnconfirmed_Tests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var password in new[] { null, "wal-secret" })
            foreach (var marker in new byte[] { 0xA5, 0xFF, 0x42 })
            foreach (var prefix in new[] { "unconfirmed", "confirmed", "confirmed-modern", "blank-header" })
            foreach (var length in new[] { 32, Constants.PAGE_SIZE - 1, Constants.PAGE_SIZE, WalChecksum.FrameSize })
                yield return new object[] { password, marker, prefix, length };
        }

        // Corrupt input: a genuine released data/WAL pair followed by a foreign page.
        // The first page is legacy, so the first-prefix startup guard cannot detect it.
        [Theory]
        [MemberData(nameof(Cases))]
        public void Later_nonlegacy_unconfirmed_page_refuses_before_cleanup_and_preserves_both_files(
            string password, byte marker, string prefix, int length)
        {
            var name = password == null ? "plain" : "encrypted";
            var data = ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip", name + ".db");
            var original = ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip", name + "-log.db");
            var legacy = ConvertedWalFiles.Plaintext(original, password);
            if (prefix == "unconfirmed")
            {
                for (var offset = 0; offset < legacy.Length; offset += Constants.PAGE_SIZE)
                    legacy[offset + BasePage.P_IS_CONFIRMED] = 0;
            }
            var page = new byte[WalChecksum.FrameSize];
            BitConverter.GetBytes(3u).CopyTo(page, BasePage.P_PAGE_ID);
            page[BasePage.P_PAGE_TYPE] = (byte)PageType.Data;
            page[BasePage.P_PAGE_FORMAT] = marker;
            BitConverter.GetBytes(0x7FFF0000u).CopyTo(page, BasePage.P_TRANSACTION_ID);
            if (prefix == "blank-header") Array.Clear(page, 0, 16);
            var tail = page.Take(length).ToArray();
            if (prefix == "confirmed-modern")
            {
                var confirmed = (byte[])page.Clone();
                confirmed[BasePage.P_IS_CONFIRMED] = 1;
                // Legacy replay sees page-aligned payloads; omit the trailer here to
                // isolate confirmation handling from the frame-layout mismatch.
                legacy = legacy.Concat(confirmed.Take(Constants.PAGE_SIZE)).ToArray();
            }
            var wal = Append(original, password, legacy.Concat(tail).ToArray());
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                foreach (var readOnly in new[] { false, true })
                {
                    File.WriteAllBytes(file.Filename, data);
                    File.WriteAllBytes(log, wal);
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        Action open = () => new LiteDatabase(new ConnectionString
                        {
                            Filename = file.Filename, Password = password,
                            ReadOnly = readOnly, LegacyIndexScan = readOnly
                        }).Dispose();
                        var error = Record.Exception(open);
                        // Check preservation even when an unsafe open succeeds or reports
                        // a different error, to distinguish refusal from destructive failure.
                        File.ReadAllBytes(file.Filename).Should().Equal(data);
                        File.Exists(log).Should().BeTrue("the unadmitted WAL must survive cleanup");
                        File.ReadAllBytes(log).Should().Equal(wal);
                        error.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_DATABASE);
                    }
                }
            }
            finally { File.Delete(log); }
        }

        private static byte[] Append(byte[] original, string password, byte[] plaintext)
        {
            using var raw = new MemoryStream();
            raw.Write(original, 0, original.Length);
            using (var factory = new StreamFactory(raw, password))
            using (var stream = factory.GetStream(true, false))
            {
                stream.Position = 0;
                var padded = new byte[(plaintext.Length + 15) / 16 * 16];
                Buffer.BlockCopy(plaintext, 0, padded, 0, plaintext.Length);
                stream.Write(padded, 0, padded.Length);
            }
            // Tear ciphertext only after encryption; AesStream requires aligned writes.
            return raw.ToArray().Take((password == null ? 0 : Constants.PAGE_SIZE) + plaintext.Length).ToArray();
        }
    }
}
