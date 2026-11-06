using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Utils;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    public class JournalRecoveryGeneration_Tests
    {
        public static IEnumerable<object[]> TornIdentities()
        {
            foreach (var password in new[] { null, "wal-secret" })
            foreach (var damage in new[] { "checksum", "salt", "salt-marker", "version", "all-format" })
            foreach (var readOnly in new[] { false, true })
                yield return new object[] { password, damage, readOnly };
        }

        [Theory]
        [MemberData(nameof(TornIdentities))]
        public void Damaged_primary_fields_do_not_override_verified_journal_recovery(
            string password, string damage, bool readOnly)
        {
            CheckRecovery(password, primary =>
            {
                var page = new BufferSlice(primary, 0, PAGE_SIZE);
                // This is the same database with torn fields, not an independently
                // created foreign database. Even plausible ticks/salt may be wrong.
                page.Write(page.ReadInt64(HeaderPage.P_CREATION_TIME) + 1, HeaderPage.P_CREATION_TIME);
                if (damage == "salt" || damage == "salt-marker") primary[WalChecksum.SaltPosition] ^= 1;
                if (damage == "salt-marker") primary[WalChecksum.MarkerPosition] ^= 1;
                if (damage == "version") primary[HeaderPage.P_FILE_VERSION] = 0;
                if (damage == "all-format")
                {
                    var creation = page.ReadInt64(HeaderPage.P_CREATION_TIME);
                    Array.Clear(primary, 0, PAGE_SIZE);
                    page.Write(creation, HeaderPage.P_CREATION_TIME);
                }
                page.ReadUInt32(BasePage.P_TRANSACTION_ID).Should().NotBe(PageChecksum.Compute(page));
            }, readOnly);
        }

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

        [Fact]
        public void Erased_AES_creation_block_with_plausible_ticks_is_not_a_foreign_identity()
        {
            const string password = "wal-secret";
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                ModernHeaderJournalPair_Tests.Capture(file.Filename, password, out _, out var primary, out var wal);
                primary = EncryptWithPlausibleBlankCreation(ConvertedWalFiles.Plaintext(primary, password), password);
                Array.Clear(primary, PAGE_SIZE + 64, 16);
                var ticks = BitConverter.ToInt64(ConvertedWalFiles.Plaintext(primary, password), HeaderPage.P_CREATION_TIME);
                ticks.Should().BeGreaterThan(0).And.BeLessThan(DateTime.MaxValue.Ticks);
                foreach (var readOnly in new[] { true, false })
                {
                    File.WriteAllBytes(file.Filename, primary);
                    File.WriteAllBytes(log, wal);
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        using (var db = new LiteDatabase(new ConnectionString
                        {
                            Filename = file.Filename, Password = password, ReadOnly = readOnly
                        }))
                        {
                            db.GetCollection("rows").FindAll().Select(x => x.ToString()).Should().Equal(
                                new BsonDocument { ["_id"] = 1, ["value"] = 42 }.ToString());
                            db.GetCollection("newrows").FindAll().Select(x => x.ToString()).Should().Equal(
                                new BsonDocument { ["_id"] = 1, ["value"] = "checkpointed-modern-only" }.ToString());
                        }
                        if (readOnly)
                        {
                            File.ReadAllBytes(file.Filename).Should().Equal(primary);
                            File.ReadAllBytes(log).Should().Equal(wal);
                        }
                    }
                }
            }
            finally { File.Delete(log); }
        }

        private static void CheckRecovery(string password, Action<byte[]> change, bool readOnly)
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                ModernHeaderJournalPair_Tests.Capture(file.Filename, password, out _, out var primary, out var wal);
                primary = ConvertedWalFiles.Rewrite(primary, password, change);
                File.WriteAllBytes(file.Filename, primary);
                File.WriteAllBytes(log, wal);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using (var db = new LiteDatabase(new ConnectionString
                    {
                        Filename = file.Filename, Password = password, ReadOnly = readOnly
                    }))
                    {
                        var expected = new BsonDocument { ["_id"] = 1, ["value"] = 42 }.ToString();
                        db.GetCollection("rows").FindAll().Select(x => x.ToString()).Should().Equal(expected);
                        db.GetCollection("rows").Find(Query.EQ("_id", 1)).Select(x => x.ToString()).Should().Equal(expected);
                        db.GetCollection("newrows").FindAll().Select(x => x.ToString()).Should().Equal(
                            new BsonDocument { ["_id"] = 1, ["value"] = "checkpointed-modern-only" }.ToString());
                    }
                    if (readOnly)
                    {
                        File.ReadAllBytes(file.Filename).Should().Equal(primary);
                        File.ReadAllBytes(log).Should().Equal(wal);
                    }
                }
            }
            finally { File.Delete(log); }
        }

        private static byte[] EncryptWithPlausibleBlankCreation(byte[] plain, string password)
        {
            // Pick a deterministic key whose zero-ciphertext block decodes to
            // valid ticks. Otherwise this regression would depend on random salts.
            for (var candidate = 0; candidate < 256; candidate++)
            {
                var salt = new byte[16];
                salt[0] = (byte)candidate;
                using var derive = new Rfc2898DeriveBytes(password, salt);
                using var aes = Aes.Create();
                aes.Key = derive.GetBytes(32);
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using var decryptor = aes.CreateDecryptor();
                var blank = decryptor.TransformFinalBlock(new byte[16], 0, 16);
                var ticks = BitConverter.ToInt64(blank, HeaderPage.P_CREATION_TIME - 64);
                if (ticks <= 0 || ticks >= DateTime.MaxValue.Ticks) continue;
                var result = new byte[PAGE_SIZE + plain.Length];
                result[0] = 1;
                Buffer.BlockCopy(salt, 0, result, 1, salt.Length);
                using var encryptor = aes.CreateEncryptor();
                var check = Enumerable.Repeat((byte)1, 32).ToArray();
                encryptor.TransformBlock(check, 0, check.Length, result, 32);
                var payload = encryptor.TransformFinalBlock(plain, 0, plain.Length);
                Buffer.BlockCopy(payload, 0, result, PAGE_SIZE, payload.Length);
                return result;
            }
            throw new InvalidOperationException("No deterministic plausible blank-block ticks found");
        }

        private static void CheckRefusal(string password, Action<byte[], byte[]> change, int code, bool? onlyReadOnly = null)
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
                foreach (var readOnly in onlyReadOnly.HasValue ? new[] { onlyReadOnly.Value } : new[] { false, true })
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
