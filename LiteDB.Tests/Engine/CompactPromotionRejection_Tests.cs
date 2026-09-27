using System;
using System.IO;
using System.Text;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection("PromotionPowerLoss")]
    public class CompactPromotionRejection_Tests
    {
        [Theory]
        [InlineData(null, "identity")]
        [InlineData("secret", "identity")]
        [InlineData(null, "version")]
        [InlineData("secret", "version")]
        [InlineData(null, "checksum")]
        [InlineData("secret", "checksum")]
        public void Damaged_journal_rejections_preserve_sources_and_valid_journal_still_recovers(
            string password, string rejection)
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                PromotionPowerLossScenario.Run(password, false, "promotion-before-header-write",
                    tornPrefix: 59, damage: true, inspectFiles: (data, log) =>
                    {
                        var controlledData = ForceRejectionPath(data, password, rejection);
                        var damagedLog = (byte[])log.Clone();
                        damagedLog[damagedLog.Length - Constants.PAGE_SIZE + 128] ^= 1;
                        File.WriteAllBytes(file.Filename, controlledData);
                        File.WriteAllBytes(logName, damagedLog);

                        foreach (var error in CompactPromotionRejection.AssertUnchanged(
                            file.Filename, password, controlledData, damagedLog))
                        {
                            if (rejection == "checksum")
                            {
                                Assert.IsType<PageChecksumException>(error);
                                Assert.Equal(LiteException.CHECKSUM_MISMATCH, error.ErrorCode);
                                Assert.Contains("Data file at position 0", error.Message);
                            }
                            else
                            {
                                Assert.IsType<LiteException>(error);
                                Assert.Equal(rejection == "identity" ? LiteException.INVALID_DATABASE :
                                    LiteException.UNSUPPORTED_FILE_VERSION, error.ErrorCode);
                            }
                        }

                        // The same damaged primary must remain recoverable when its
                        // genuine journal is supplied, ruling out unconditional rejection.
                        File.WriteAllBytes(logName, log);
                        foreach (var readOnly in new[] { true, false, false })
                        {
                            using (var db = new LiteDatabase(new ConnectionString
                            {
                                Filename = file.Filename, Password = password, ReadOnly = readOnly
                            }))
                                PromotionPowerLossScenario.Verify(db, false, false);
                            if (readOnly)
                            {
                                Assert.Equal(controlledData, File.ReadAllBytes(file.Filename));
                                Assert.Equal(log, File.ReadAllBytes(logName));
                            }
                        }
                    });
            }
            finally { File.Delete(logName); }
        }

        private static byte[] ForceRejectionPath(byte[] data, string password, string rejection)
        {
            using var raw = new MemoryStream((byte[])data.Clone());
            if (password == null)
            {
                RewriteHeader(raw, rejection);
                return raw.ToArray();
            }
            using (var encrypted = new AesStream(password, raw, allowRecovery: false))
                RewriteHeader(encrypted, rejection);
            return raw.ToArray();
        }

        private static void RewriteHeader(Stream stream, string rejection)
        {
            var bytes = new byte[Constants.PAGE_SIZE];
            stream.Position = 0;
            stream.ReadRequired(bytes, 0, bytes.Length);
            // A torn encrypted block can decode to any version. Force each
            // validation branch independently of the randomly generated AES salt.
            bytes[HeaderPage.P_FILE_VERSION] = rejection == "checksum" ? HeaderPage.INDEX_FILE_VERSION : (byte)0;
            bytes[BasePage.P_PAGE_FORMAT] = PageChecksum.Legacy;
            var identity = Encoding.ASCII.GetBytes(HeaderPage.HEADER_INFO);
            Buffer.BlockCopy(identity, 0, bytes, HeaderPage.P_HEADER_INFO, identity.Length);
            if (rejection == "identity") bytes[HeaderPage.P_HEADER_INFO] = 0;
            new BufferSlice(bytes, 0, bytes.Length).Write(0u, WalChecksum.MarkerPosition);
            stream.Position = 0;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }

    internal static class CompactPromotionRejection
    {
        internal static LiteException[] AssertUnchanged(string filename, string password, byte[] data, byte[] log)
        {
            var errors = new LiteException[3];
            var modes = new[] { true, false, false };
            for (var attempt = 0; attempt < modes.Length; attempt++)
            {
                var error = Record.Exception(() =>
                {
                    using var db = new LiteDatabase(new ConnectionString
                    {
                        Filename = filename, Password = password, ReadOnly = modes[attempt]
                    });
                });
                // Preserve the source oracle even if the rejection contract regresses.
                Assert.Equal(data, File.ReadAllBytes(filename));
                Assert.Equal(log, File.ReadAllBytes(FileHelper.GetLogFile(filename)));
                errors[attempt] = Assert.IsAssignableFrom<LiteException>(error);
                Assert.Contains(errors[attempt].ErrorCode,
                    new[] { LiteException.INVALID_DATABASE, LiteException.UNSUPPORTED_FILE_VERSION,
                        LiteException.CHECKSUM_MISMATCH });
            }
            return errors;
        }
    }
}
