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
    public class ConvertedWalFormat_Tests
    {
        public static IEnumerable<object[]> DamageCases()
        {
            foreach (var password in new[] { null, "migration-power-loss" })
            foreach (var version in new byte[] { 8, 9 })
            foreach (var damage in new[] { "incomplete-page", "absent-trailer", "partial-trailer", "crc", "payload", "intact", "format" })
                yield return new object[] { password, version, damage };
        }

        // Deliberately mismatched pairs: restore/copy skew or loss of a synced header plus a
        // damaged modern WAL. This does not model an ordinary crash on sync-honoring storage.
        [Theory]
        [MemberData(nameof(DamageCases))]
        public void Damaged_modern_wal_beside_legacy_data_refuses_twice_without_mutation(string password, byte version, string damage)
        {
            using var files = new ConvertedWalFiles(password, version);
            var wal = files.Damage(damage);
            files.Install(files.Legacy, wal);
            foreach (var readOnly in new[] { false, true })
                AssertRefusedTwice(files, files.Legacy, wal, readOnly);
        }

        [Theory]
        [InlineData(null, 0xA5)]
        [InlineData(null, 0xFF)]
        [InlineData("migration-power-loss", 0xA5)]
        [InlineData("migration-power-loss", 0xFF)]
        public void First_page_header_alone_identifies_a_nonlegacy_wal(string password, byte marker)
        {
            using var files = new ConvertedWalFiles(password);
            var wal = ConvertedWalFiles.Rewrite(files.Wal, password, bytes => bytes[BasePage.P_PAGE_FORMAT] = marker)
                .Take((password == null ? 0 : Constants.PAGE_SIZE) + BasePage.P_PAGE_FORMAT + 1).ToArray();
            files.Install(files.Legacy, wal);
            AssertRefusedTwice(files, files.Legacy, wal, false);
        }

        [Theory]
        [InlineData(17)]
        [InlineData(31)]
        [InlineData(4096)]
        [InlineData(8191)]
        public void Incomplete_encrypted_preamble_does_not_hide_legacy_data(int length)
        {
            using var files = new ConvertedWalFiles("migration-power-loss");
            var expected = ReadRows(files);
            files.Install(files.Legacy, files.Wal.Take(length).ToArray());
            for (var attempt = 0; attempt < 2; attempt++)
                ReadRows(files).Should().Equal(expected);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("migration-power-loss")]
        public void Matching_modern_header_still_opens_with_a_truncated_first_frame(string password)
        {
            using var files = new ConvertedWalFiles(password);
            var expected = ReadRows(files);
            foreach (var damage in new[] { "incomplete-page", "absent-trailer", "partial-trailer" })
            {
                files.Install(files.Modern, files.Damage(damage));
                for (var attempt = 0; attempt < 2; attempt++)
                    ReadRows(files).Should().Equal(expected);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("wal-secret")]
        public void Genuine_5_0_21_wal_recovers_both_rows_and_reopens(string password)
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            var name = password == null ? "plain" : "encrypted";
            var data = ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip", name + ".db");
            var wal = ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip", name + "-log.db");
            var plainData = ConvertedWalFiles.Plaintext(data, password);
            plainData[HeaderPage.P_FILE_VERSION].Should().Be(8);
            var plainWal = ConvertedWalFiles.Plaintext(wal, password);
            plainWal.Length.Should().BeGreaterThan(0);
            plainWal[BasePage.P_PAGE_FORMAT].Should().Be(PageChecksum.Legacy);
            try
            {
                // Independent control proves row 2 is WAL-only in the released-writer image.
                File.WriteAllBytes(file.Filename, data);
                using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password }))
                    db.GetCollection("rows").FindAll().Select(x => x.ToString()).Should()
                        .Equal(new BsonDocument { ["_id"] = 1, ["value"] = 7 }.ToString());
                File.WriteAllBytes(file.Filename, data);
                File.WriteAllBytes(log, wal);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password });
                    db.GetCollection("rows").FindAll().OrderBy(x => x["_id"]).Select(x => x.ToString()).Should().Equal(
                        new BsonDocument { ["_id"] = 1, ["value"] = 7 }.ToString(),
                        new BsonDocument { ["_id"] = 2, ["value"] = 8 }.ToString());
                }
            }
            finally { File.Delete(log); }
        }

        [Theory]
        [InlineData(0xA5)]
        [InlineData(0xFF)]
        public void Later_nonlegacy_committed_page_is_not_admitted_to_legacy_replay(byte marker)
        {
            using var files = new ConvertedWalFiles(null);
            var legacyWal = ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip", "plain-log.db");
            var page = new byte[Constants.PAGE_SIZE];
            BitConverter.GetBytes(3u).CopyTo(page, BasePage.P_PAGE_ID);
            page[BasePage.P_PAGE_TYPE] = (byte)PageType.Data;
            page[BasePage.P_PAGE_FORMAT] = marker;
            page[BasePage.P_IS_CONFIRMED] = 1;
            BitConverter.GetBytes(0x7FFF0000u).CopyTo(page, BasePage.P_TRANSACTION_ID);
            var wal = legacyWal.Concat(page).ToArray();
            var data = ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip", "plain.db");
            files.Install(data, wal);
            AssertRefusedTwice(files, data, wal, false);
        }

        private static string[] ReadRows(ConvertedWalFiles files)
        {
            using var db = files.Open();
            return db.GetCollection("rows").FindAll().OrderBy(x => x["_id"]).Select(x => x.ToString()).ToArray();
        }

        private static void AssertRefusedTwice(ConvertedWalFiles files, byte[] data, byte[] wal, bool readOnly)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Action open = () => files.Open(readOnly).Dispose();
                open.Should().Throw<LiteException>().Where(x => x.ErrorCode == LiteException.INVALID_DATABASE)
                    .WithMessage("*log*data file*");
                File.ReadAllBytes(files.Filename).Should().Equal(data);
                File.Exists(files.LogName).Should().BeTrue("recovery evidence must survive refusal and cleanup");
                File.ReadAllBytes(files.LogName).Should().Equal(wal);
            }
        }
    }
}
