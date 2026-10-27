using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    [Trait("Category", "IoSafety")]
    public class ConvertedWalJournal_Tests
    {
        public static IEnumerable<object[]> ForeignPages()
        {
            foreach (var password in new[] { null, "wal-secret" })
            foreach (var marker in new byte[] { 0xA5, 0xFF })
            foreach (var length in new[] { 32, PAGE_SIZE, WalChecksum.FrameSize })
                yield return new object[] { password, marker, length };
        }

        // Corrupt input: the footer binds only its prefix, so appending a foreign
        // page leaves a fully valid journal discoverable through its intent page.
        [Theory]
        [MemberData(nameof(ForeignPages))]
        public void Foreign_page_after_valid_legacy_journal_refuses_before_header_repair(
            string password, byte marker, int length)
        {
            var data = LegacyData(password);
            var wal = Journal(data, password, ForeignPage(marker).Take(length).ToArray());
            using (var raw = new MemoryStream(wal))
            using (var factory = new StreamFactory(raw, password))
            using (var stream = factory.GetStream(false, false))
            {
                var journal = HeaderJournal.Read(stream);
                journal.Should().NotBeNull();
                journal.ConfirmsLegacyBackup.Should().BeTrue();
                journal.IntentOnly.Should().BeFalse();
                journal.Position.Should().Be(2L * PAGE_SIZE, "the valid journal precedes the foreign tail");
                journal.Header.Should().NotEqual(ConvertedWalFiles.Plaintext(data, password).Take(PAGE_SIZE),
                    "repairing the primary from this journal would change its bytes");
            }
            AssertRefusedTwice(data, wal, password, marker, 4L * PAGE_SIZE + BasePage.P_PAGE_FORMAT);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("wal-secret")]
        public void Valid_legacy_journal_and_later_legacy_commit_still_recover(string password)
        {
            var data = LegacyData(password);
            var header = ConvertedWalFiles.Plaintext(data, password).Take(PAGE_SIZE).ToArray();
            var page = new BufferSlice(header, 0, PAGE_SIZE);
            page.Write(2u, BasePage.P_TRANSACTION_ID);
            page.Write(true, BasePage.P_IS_CONFIRMED);
            page.Write(123, EnginePragmas.P_USER_VERSION);
            foreach (var tail in new[] { new byte[0], header })
            {
                using var file = new TempFile();
                var log = FileHelper.GetLogFile(file.Filename);
                try
                {
                    File.WriteAllBytes(file.Filename, data);
                    File.WriteAllBytes(log, Journal(data, password, tail));
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password });
                        db.UserVersion.Should().Be(tail.Length == 0 ? 0 : 123);
                        db.GetCollection("rows").FindAll().Select(x => x.ToString()).Should().Equal(
                            new BsonDocument { ["_id"] = 1, ["value"] = 7 }.ToString());
                    }
                }
                finally { File.Delete(log); }
            }
        }

        [Theory]
        [InlineData(null, 0xA5)]
        [InlineData("wal-secret", 0xA5)]
        [InlineData(null, 0xFF)]
        [InlineData("wal-secret", 0xFF)]
        public void Invalid_page_type_does_not_mask_later_nonlegacy_format(string password, byte marker)
        {
            var data = LegacyData(password);
            var name = password == null ? "plain" : "encrypted";
            var original = ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip", name + "-log.db");
            var legacy = ConvertedWalFiles.Plaintext(original, password);
            using var raw = new MemoryStream();
            using (var factory = new StreamFactory(raw, password))
            using (var stream = factory.GetStream(true, false))
            {
                stream.Write(legacy, 0, legacy.Length);
                var page = ForeignPage(marker);
                page[BasePage.P_PAGE_TYPE] = 0xFE;
                stream.Write(page, 0, PAGE_SIZE);
            }
            AssertRefusedTwice(data, raw.ToArray(), password, marker, legacy.Length + BasePage.P_PAGE_FORMAT);
        }

        public static IEnumerable<object[]> GarbageTails()
        {
            foreach (var password in new[] { null, "wal-secret" })
            foreach (var marker in new byte[] { 0xA5, 0xFF, 0x42 })
            foreach (var typed in new[] { false, true })
                yield return new object[] { password, marker, typed };
        }

        [Theory]
        [MemberData(nameof(GarbageTails))]
        public void Nonpage_initial_tail_is_not_mistaken_for_a_format_marker(string password, byte marker, bool typed)
        {
            var data = LegacyData(password);
            using var raw = new MemoryStream();
            using (var factory = new StreamFactory(raw, password))
            using (var stream = factory.GetStream(true, false))
            {
                var garbage = Enumerable.Repeat((byte)0xCD, 32).ToArray();
                if (typed)
                {
                    garbage[BasePage.P_PAGE_TYPE] = (byte)PageType.Data;
                    garbage[BasePage.P_IS_CONFIRMED] = 0;
                }
                garbage[BasePage.P_PAGE_FORMAT] = marker;
                stream.Write(garbage, 0, garbage.Length);
            }
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                foreach (var readOnly in new[] { true, false })
                {
                    File.WriteAllBytes(file.Filename, data);
                    File.WriteAllBytes(log, raw.ToArray());
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        using (var db = new LiteDatabase(new ConnectionString
                        {
                            Filename = file.Filename, Password = password,
                            ReadOnly = readOnly, LegacyIndexScan = readOnly
                        }))
                            db.GetCollection("rows").FindAll().Select(x => x.ToString()).Should().Equal(
                                new BsonDocument { ["_id"] = 1, ["value"] = 7 }.ToString());
                        if (readOnly)
                        {
                            File.ReadAllBytes(file.Filename).Should().Equal(data);
                            File.ReadAllBytes(log).Should().Equal(raw.ToArray());
                        }
                    }
                }
            }
            finally { File.Delete(log); }
        }

        private static byte[] LegacyData(string password) => ConvertedWalFiles.Entry(
            "LegacyWal_5_0_21.zip", password == null ? "plain.db" : "encrypted.db");

        private static byte[] ForeignPage(byte marker)
        {
            var page = new byte[WalChecksum.FrameSize];
            var buffer = new BufferSlice(page, 0, PAGE_SIZE);
            buffer.Write(3u, BasePage.P_PAGE_ID);
            buffer.Write((byte)PageType.Data, BasePage.P_PAGE_TYPE);
            buffer.Write(0x7FFF0000u, BasePage.P_TRANSACTION_ID);
            buffer.Write(marker, BasePage.P_PAGE_FORMAT);
            return page;
        }

        private static byte[] Journal(byte[] data, string password, byte[] tail)
        {
            var header = ConvertedWalFiles.Plaintext(data, password).Take(PAGE_SIZE).ToArray();
            using var raw = new MemoryStream();
            using (var factory = new StreamFactory(raw, password))
            using (var stream = factory.GetStream(true, false))
            {
                HeaderJournal.BackupLegacyHeader(stream, header);
                HeaderJournal.Write(stream, header, conversion: true, new WalChecksum());
                stream.Position = stream.Length;
                stream.Write(tail, 0, tail.Length);
            }
            return raw.ToArray();
        }

        private static void AssertRefusedTwice(byte[] data, byte[] wal, string password, byte marker, long offset)
        {
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
                        var error = Record.Exception(() => new LiteDatabase(new ConnectionString
                        {
                            Filename = file.Filename, Password = password,
                            ReadOnly = readOnly, LegacyIndexScan = readOnly
                        }).Dispose());
                        File.ReadAllBytes(file.Filename).Should().Equal(data);
                        File.Exists(log).Should().BeTrue();
                        File.ReadAllBytes(log).Should().Equal(wal);
                        var refusal = error.Should().BeOfType<LiteException>().Subject;
                        refusal.ErrorCode.Should().Be(LiteException.INVALID_DATABASE);
                        refusal.Message.Should().Contain($"non-legacy format 0x{marker:X2} at plaintext offset {offset}")
                            .And.Contain("preserve both files for recovery");
                    }
                }
            }
            finally { File.Delete(log); }
        }
    }
}
