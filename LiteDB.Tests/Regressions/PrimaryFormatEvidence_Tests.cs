using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    [Trait("Category", "IoSafety")]
    public class PrimaryFormatEvidence_Tests
    {
        [Theory]
        [InlineData(null, 0)]
        [InlineData(null, 7)]
        [InlineData(null, 8)]
        [InlineData(null, 9)]
        [InlineData(null, 14)]
        [InlineData(null, 255)]
        [InlineData("wal-secret", 0)]
        [InlineData("wal-secret", 7)]
        [InlineData("wal-secret", 8)]
        [InlineData("wal-secret", 9)]
        [InlineData("wal-secret", 14)]
        [InlineData("wal-secret", 255)]
        public void Checksum_valid_unsupported_version_refuses_without_mutation(string password, byte version)
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                ModernHeaderJournalPair_Tests.Capture(file.Filename, password, out _, out var data, out var wal);
                data = ConvertedWalFiles.Rewrite(data, password, bytes =>
                {
                    bytes[HeaderPage.P_FILE_VERSION] = version;
                    PageChecksum.Write(new BufferSlice(bytes, 0, PAGE_SIZE));
                });
                foreach (var readOnly in new[] { true, false })
                {
                    File.WriteAllBytes(file.Filename, data);
                    File.WriteAllBytes(log, wal);
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        Action open = () => new LiteDatabase(new ConnectionString
                        {
                            Filename = file.Filename, Password = password, ReadOnly = readOnly
                        }).Dispose();
                        open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_DATABASE);
                        File.ReadAllBytes(file.Filename).Should().Equal(data);
                        File.ReadAllBytes(log).Should().Equal(wal);
                    }
                }
            }
            finally { File.Delete(log); }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("wal-secret")]
        public void Fully_legacy_shaped_header_with_modern_body_still_recovers(string password)
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                ModernHeaderJournalPair_Tests.Capture(file.Filename, password, out var legacy, out var modern, out var wal);
                var oldHeader = ConvertedWalFiles.Plaintext(legacy, password);
                var data = ConvertedWalFiles.Rewrite(modern, password, bytes =>
                {
                    Buffer.BlockCopy(oldHeader, 0, bytes, 0, PAGE_SIZE);
                    // Defeat a length-only distinction too: independent modern
                    // pages must veto legacy identification after a stale header write.
                    new BufferSlice(bytes, 0, PAGE_SIZE).Write((uint)(bytes.Length / PAGE_SIZE - 1), HeaderPage.P_LAST_PAGE_ID);
                });
                var plain = ConvertedWalFiles.Plaintext(data, password);
                using (var stream = new MemoryStream(plain))
                    LegacyPrimaryLayout.IsIdentified(stream, new BufferSlice(plain, 0, PAGE_SIZE)).Should().BeFalse();
                foreach (var readOnly in new[] { true, false })
                {
                    File.WriteAllBytes(file.Filename, data);
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
                            db.GetCollection("rows").Find(Query.EQ("_id", 1)).Single()["value"].AsInt32.Should().Be(42);
                            db.GetCollection("newrows").FindAll().Single()["value"].AsString.Should().Be("checkpointed-modern-only");
                        }
                        if (readOnly)
                        {
                            File.ReadAllBytes(file.Filename).Should().Equal(data);
                            File.ReadAllBytes(log).Should().Equal(wal);
                        }
                    }
                }
            }
            finally { File.Delete(log); }
        }

        [Fact]
        public void Modern_signature_cannot_clear_into_complete_v7_signature()
        {
            var signature = Encoding.ASCII.GetBytes(HeaderPage.HEADER_INFO);
            signature.Length.Should().Be(27);
            // At byte 33 a modern header has '*', whereas v7 requires 'i'.
            // Clearing bits/bytes or replaying a modern header cannot add those bits.
            signature[1].Should().Be((byte)'*');
            signature[33 - 25].Should().Be((byte)'i');
            (signature[33 - 25] & ~signature[1]).Should().NotBe(0);
            var header = new byte[PAGE_SIZE];
            Buffer.BlockCopy(signature, 0, header, 25, signature.Length);
            header[52] = 7;
            FileReaderV7.IsVersion(header).Should().BeTrue();
            for (var i = 0; i < signature.Length; i++)
            {
                header[25 + i] ^= 1;
                FileReaderV7.IsVersion(header).Should().BeFalse("every byte of the v7 signature must match");
                header[25 + i] ^= 1;
            }
            header[52] = 8;
            FileReaderV7.IsVersion(header).Should().BeFalse();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("wal-secret")]
        public void Full_stale_header_after_header_only_cutover_is_indistinguishable_and_refuses(string password)
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                var legacy = ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip", password == null ? "plain.db" : "encrypted.db");
                File.WriteAllBytes(file.Filename, legacy);
                var settings = new EngineSettings { Filename = file.Filename, Password = password };
                using (var disk = new DiskService(settings, new EngineState(null, settings), new[] { 2 }))
                {
                    var header = new HeaderPage(disk.TakeOpeningHeader());
                    disk.EnableChecksums(ref header);
                }
                var modern = File.ReadAllBytes(file.Filename);
                var oldPlain = ConvertedWalFiles.Plaintext(legacy, password);
                var newPlain = ConvertedWalFiles.Plaintext(modern, password);
                newPlain.Skip(PAGE_SIZE).Should().Equal(oldPlain.Skip(PAGE_SIZE), "cutover writes only the header");
                newPlain[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.CHECKSUM_FILE_VERSION);
                PageChecksum.Validate(new BufferSlice(newPlain, 0, PAGE_SIZE), 0);
                // Build the empty, valid modern checkpoint journal for this exact
                // cutover state; then model a full stale pre-cutover header write.
                using (var raw = new FileStream(log, FileMode.Create, FileAccess.ReadWrite))
                using (var factory = new StreamFactory(raw, password))
                using (var stream = factory.GetStream(true, false))
                {
                    var salt = newPlain.Skip(WalChecksum.SaltPosition).Take(16).ToArray();
                    var checksums = new WalChecksum();
                    checksums.Reset(salt);
                    HeaderJournal.Write(stream, newPlain.Take(PAGE_SIZE).ToArray(), false, checksums, false, s => s.Flush());
                }
                var wal = File.ReadAllBytes(log);
                var stale = ConvertedWalFiles.Rewrite(modern, password,
                    bytes => Buffer.BlockCopy(oldPlain, 0, bytes, 0, PAGE_SIZE));
                stale.Should().Equal(legacy, "the entire primary is byte-indistinguishable from the released file");
                foreach (var readOnly in new[] { true, false })
                {
                    File.WriteAllBytes(file.Filename, stale);
                    File.WriteAllBytes(log, wal);
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        Action open = () => new LiteDatabase(new ConnectionString
                        {
                            Filename = file.Filename, Password = password, ReadOnly = readOnly
                        }).Dispose();
                        open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_DATABASE);
                        File.ReadAllBytes(file.Filename).Should().Equal(stale);
                        File.ReadAllBytes(log).Should().Equal(wal);
                    }
                }
            }
            finally { File.Delete(log); }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("wal-secret")]
        public void Legacy_layout_identification_requires_all_three_page_kinds_and_restores_position(string password)
        {
            var data = ConvertedWalFiles.Plaintext(ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip",
                password == null ? "plain.db" : "encrypted.db"), password);
            var header = new BufferSlice(data, 0, PAGE_SIZE);
            using var stream = new MemoryStream(data);
            stream.Position = 37;
            LegacyPrimaryLayout.IsIdentified(stream, header).Should().BeTrue();
            stream.Position.Should().Be(37);
            for (var page = 1; page <= 3; page++)
            {
                var offset = page * PAGE_SIZE + BasePage.P_PAGE_FORMAT;
                data[offset] = PageChecksum.Checksummed;
                LegacyPrimaryLayout.IsIdentified(stream, header).Should().BeFalse();
                stream.Position.Should().Be(37);
                data[offset] = 0;
            }
            // One legacy-looking page alone never identifies the whole primary.
            Array.Clear(data, 2 * PAGE_SIZE, 2 * PAGE_SIZE);
            LegacyPrimaryLayout.IsIdentified(stream, header).Should().BeFalse();
            stream.Position.Should().Be(37);
        }

        [Theory]
        [InlineData(PAGE_SIZE)]
        [InlineData(3 * PAGE_SIZE)]
        public void Independent_page_read_failure_preserves_bytes_and_stream_position(int failureOffset)
        {
            var data = ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip", "plain.db");
            var before = (byte[])data.Clone();
            using var stream = new FailingPageRead(data, failureOffset);
            stream.Position = 37;
            Action identify = () => LegacyPrimaryLayout.IsIdentified(stream, new BufferSlice(data, 0, PAGE_SIZE));
            identify.Should().Throw<IOException>().WithMessage("independent page read failed");
            stream.Position.Should().Be(37);
            stream.ToArray().Should().Equal(before);
        }

        private sealed class FailingPageRead : MemoryStream
        {
            private readonly int _failureOffset;
            internal FailingPageRead(byte[] bytes, int failureOffset) : base(bytes) => _failureOffset = failureOffset;
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (Position >= _failureOffset) throw new IOException("independent page read failed");
                return base.Read(buffer, offset, count);
            }
        }
    }
}
