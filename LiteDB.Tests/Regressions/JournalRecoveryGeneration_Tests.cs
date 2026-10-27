using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Utils;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    public class JournalRecoveryGeneration_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("wal-secret")]
        public void Checksum_valid_primary_of_another_creation_refuses_without_mutation(string password)
        {
            CheckRefusal(password, (primary, wal) =>
            {
                var header = new BufferSlice(primary, 0, PAGE_SIZE);
                header.Write(header.ReadInt64(HeaderPage.P_CREATION_TIME) + 1, HeaderPage.P_CREATION_TIME);
                PageChecksum.Write(header);
            }, LiteException.INVALID_DATABASE);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("wal-secret")]
        public void Erased_primary_requires_WAL_generation_not_just_a_valid_footer(string password)
        {
            CheckRefusal(password, (primary, wal) =>
            {
                Array.Clear(primary, 0, PAGE_SIZE);
                // Rebind the footer CRC and physical-body CRC after changing a
                // frame's generation. Only actual frame verification detects it.
                wal[PAGE_SIZE + 8] ^= 1;
                var position = wal.Length - HeaderJournal.Size;
                var descriptor = new BufferSlice(wal, position + PAGE_SIZE, PAGE_SIZE);
                descriptor.Write(~Crc32C.Update(uint.MaxValue, wal, 0, position), 152);
                descriptor.Write(0u, 148);
                descriptor.Write(~Crc32C.Update(uint.MaxValue, wal, position, HeaderJournal.Size), 148);
            }, LiteException.CHECKSUM_MISMATCH);
        }

        private static void CheckRefusal(string password, Action<byte[], byte[]> change, int code)
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                ModernHeaderJournalPair_Tests.Capture(file.Filename, password, out _, out var primary, out var wal);
                var plainPrimary = ConvertedWalFiles.Plaintext(primary, password);
                var plainWal = ConvertedWalFiles.Plaintext(wal, password);
                change(plainPrimary, plainWal);
                primary = ConvertedWalFiles.Rewrite(primary, password, bytes => Buffer.BlockCopy(plainPrimary, 0, bytes, 0, plainPrimary.Length));
                wal = ConvertedWalFiles.Rewrite(wal, password, bytes => Buffer.BlockCopy(plainWal, 0, bytes, 0, plainWal.Length));
                foreach (var readOnly in new[] { false, true })
                {
                    File.WriteAllBytes(file.Filename, primary);
                    File.WriteAllBytes(log, wal);
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        var error = Record.Exception(() => new LiteDatabase(new ConnectionString
                        {
                            Filename = file.Filename, Password = password, ReadOnly = readOnly
                        }).Dispose());
                        File.ReadAllBytes(file.Filename).Should().Equal(primary);
                        File.ReadAllBytes(log).Should().Equal(wal);
                        error.Should().BeAssignableTo<LiteException>().Which.ErrorCode.Should().Be(code);
                    }
                }
            }
            finally { File.Delete(log); }
        }
    }
}
