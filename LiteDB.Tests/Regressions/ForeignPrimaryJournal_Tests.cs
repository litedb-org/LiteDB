using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    [Trait("Category", "IoSafety")]
    public class ForeignPrimaryJournal_Tests
    {
        public static IEnumerable<object[]> ReleasedPrimaries()
        {
            foreach (var readOnly in new[] { false, true })
            {
                yield return new object[] { "v4.db", null, readOnly, LiteException.INVALID_DATABASE };
                // v4 encryption leaves the v7 header/signature in plaintext.
                yield return new object[] { "Issue_2494_EncryptedV4.db", null, readOnly, LiteException.INVALID_DATABASE };
                yield return new object[] { "Issue_2494_EncryptedV4.db", "pass123", readOnly, LiteException.NOT_ENCRYPTED };
                yield return new object[] { "v5", null, readOnly, LiteException.INVALID_DATABASE };
                yield return new object[] { "v5", "wal-secret", readOnly, LiteException.INVALID_DATABASE };
            }
        }

        [Theory]
        [MemberData(nameof(ReleasedPrimaries))]
        public void Released_legacy_primary_beside_modern_journal_refuses_without_changing_either_file(
            string fixture, string password, bool readOnly, int code)
        {
            using var file = new TempFile();
            try
            {
                ModernHeaderJournalPair_Tests.Capture(file.Filename, fixture == "v5" ? password : null, out _, out _, out var wal);
                var primary = fixture == "v5"
                    ? ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip", password == null ? "plain.db" : "encrypted.db")
                    : File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "../../../Resources/", fixture));
                if (fixture != "v5") FileReaderV7.IsVersion(primary).Should().BeTrue();
                RefuseTwice(file.Filename, primary, wal, password, readOnly, code);
            }
            finally { File.Delete(FileHelper.GetLogFile(file.Filename)); }
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("wal-secret", false)]
        [InlineData("wal-secret", true)]
        public void Intact_other_modern_database_beside_journal_refuses_without_changing_either_file(string password, bool readOnly)
        {
            using var file = new TempFile();
            using var other = new TempFile();
            try
            {
                ModernHeaderJournalPair_Tests.Capture(file.Filename, password, out _, out var original, out var wal);
                using (var db = new LiteDatabase(new ConnectionString { Filename = other.Filename, Password = password }))
                    db.GetCollection("foreign").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "preserve me" });
                var primary = File.ReadAllBytes(other.Filename);
                var header = ConvertedWalFiles.Plaintext(primary, password);
                FileReaderV8.IsVersion(header).Should().BeTrue();
                BitConverter.ToInt64(header, HeaderPage.P_CREATION_TIME).Should().NotBe(
                    BitConverter.ToInt64(ConvertedWalFiles.Plaintext(original, password), HeaderPage.P_CREATION_TIME));
                RefuseTwice(file.Filename, primary, wal, password, readOnly, LiteException.INVALID_DATABASE);
            }
            finally { File.Delete(FileHelper.GetLogFile(file.Filename)); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Encryption_mismatched_primary_beside_plain_modern_journal_is_preserved(bool encryptedPrimary, bool readOnly)
        {
            using var file = new TempFile();
            using var other = new TempFile();
            try
            {
                ModernHeaderJournalPair_Tests.Capture(file.Filename, null, out _, out _, out var wal);
                using (var db = new LiteDatabase(new ConnectionString
                {
                    Filename = other.Filename, Password = encryptedPrimary ? "wal-secret" : null
                })) db.GetCollection("foreign").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "preserve me" });
                // No password exposes the encrypted preamble rather than a page;
                // supplying a password for plain data refuses in the stream layer.
                RefuseTwice(file.Filename, File.ReadAllBytes(other.Filename), wal,
                    encryptedPrimary ? null : "wal-secret", readOnly,
                    encryptedPrimary ? LiteException.INVALID_DATABASE : LiteException.NOT_ENCRYPTED);
            }
            finally { File.Delete(FileHelper.GetLogFile(file.Filename)); }
        }

        private static void RefuseTwice(string filename, byte[] primary, byte[] wal, string password, bool readOnly, int code)
        {
            var log = FileHelper.GetLogFile(filename);
            File.WriteAllBytes(filename, primary);
            File.WriteAllBytes(log, wal);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var error = Record.Exception(() => new LiteDatabase(new ConnectionString
                {
                    Filename = filename, Password = password, ReadOnly = readOnly
                }).Dispose());
                File.ReadAllBytes(filename).Should().Equal(primary, "a foreign journal must never replace an identifiable primary");
                File.Exists(log).Should().BeTrue();
                File.ReadAllBytes(log).Should().Equal(wal, "refusal must preserve the recovery evidence");
                error.Should().BeAssignableTo<LiteException>().Which.ErrorCode.Should().Be(code);
            }
        }
    }
}
