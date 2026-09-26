using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class LazyChecksumMarker_Tests
    {
        [Fact]
        public void MixedCoverageRejectsEveryUnknownMarkerAndEverySingleBitDowngrade()
        {
            var policy = new DataChecksumPolicy();
            policy.InitializeMixed(10);
            var page = new BufferSlice(new byte[PAGE_SIZE], 0, PAGE_SIZE);
            page.Write(3u, BasePage.P_PAGE_ID);
            page.Write(123u, BasePage.P_TRANSACTION_ID);
            policy.Validate(page, 3L * PAGE_SIZE); // Legacy transaction ID is not a CRC.
            PageChecksum.Write(page);
            policy.Validate(page, 3L * PAGE_SIZE);
            for (var value = 1; value <= 255; value++)
            {
                if (value == PageChecksum.Checksummed) continue;
                page[BasePage.P_PAGE_FORMAT] = (byte)value;
                // Even a correct CRC cannot authorize an unknown/extended page format.
                page.Write(PageChecksum.Compute(page), BasePage.P_TRANSACTION_ID);
                Assert.Throws<PageChecksumException>(() => policy.Validate(page, 3L * PAGE_SIZE));
            }
            foreach (var marker in new[] { PageChecksum.Legacy, PageChecksum.Checksummed })
                for (var bit = 0; bit < 8; bit++)
                {
                    page[BasePage.P_PAGE_FORMAT] = (byte)(marker ^ (1 << bit));
                    Assert.Throws<PageChecksumException>(() => policy.Validate(page, 3L * PAGE_SIZE));
                }
        }

        [Fact]
        public void HeaderAndPagesPastLegacyBoundaryAlwaysRequireChecksums()
        {
            var mixed = new DataChecksumPolicy();
            mixed.InitializeMixed(10);
            var complete = new DataChecksumPolicy();
            foreach (var id in new[] { 0u, 1u, 10u, 11u })
            {
                var page = new BufferSlice(new byte[PAGE_SIZE], 0, PAGE_SIZE);
                page.Write(id, BasePage.P_PAGE_ID);
                Assert.Throws<PageChecksumException>(() => complete.Validate(page, id * (long)PAGE_SIZE));
                if (id == 0 || id > 10)
                    Assert.Throws<PageChecksumException>(() => mixed.Validate(page, id * (long)PAGE_SIZE));
                else mixed.Validate(page, id * (long)PAGE_SIZE);
                PageChecksum.Write(page);
                mixed.Validate(page, id * (long)PAGE_SIZE);
                complete.Validate(page, id * (long)PAGE_SIZE);
                page[400] ^= 1;
                Assert.Throws<PageChecksumException>(() => mixed.Validate(page, id * (long)PAGE_SIZE));
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void ExtendedDataPageIsRejectedBeforeParsingOrModification(string password)
        {
            using var source = new WalTestDatabase(password);
            source.Seed("docs");
            source.Database.Checkpoint();
            using var data = ChecksumTestFiles.Copy(source.Data.ToArray());
            using var log = new MemoryStream();
            ChecksumTestFiles.MakeLegacy(data, log, password);
            using (var converted = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password })) { }
            using (var factory = new StreamFactory(data, password))
            using (var stream = factory.GetStream(true, false))
            {
                var page = new byte[PAGE_SIZE];
                stream.Position = PAGE_SIZE;
                stream.ReadRequired(page, 0, page.Length);
                page[BasePage.P_PAGE_FORMAT] = PageChecksum.Extended;
                var slice = new BufferSlice(page, 0, PAGE_SIZE);
                slice.Write(PageChecksum.Compute(slice), BasePage.P_TRANSACTION_ID);
                stream.Position = PAGE_SIZE;
                stream.Write(page, 0, page.Length);
            }
            var before = data.ToArray();
            var originalLog = log.ToArray();
            foreach (var readOnly in new[] { true, false })
            {
                Action read = () =>
                {
                    using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly });
                    using var db = new LiteDatabase(engine, disposeOnClose: false);
                    db.GetCollection("docs").FindAll().ToArray();
                };
                read.Should().Throw<PageChecksumException>();
                data.ToArray().Should().Equal(before);
                log.ToArray().Should().Equal(originalLog);
            }
        }

        [Fact]
        public void WalRequiresCurrentPageMarkerEvenWithAValidFrameCrc()
        {
            var checksum = new WalChecksum();
            checksum.Reset(Guid.NewGuid().ToByteArray());
            var page = new BufferSlice(new byte[PAGE_SIZE], 0, PAGE_SIZE);
            var metadata = new BufferSlice(new byte[WalChecksum.MetadataSize], 0, WalChecksum.MetadataSize);
            foreach (var marker in new[] { PageChecksum.Legacy, PageChecksum.Extended, (byte)1 })
            {
                checksum.Prepare(page, metadata, 0);
                page[BasePage.P_PAGE_FORMAT] = marker;
                metadata.Write(0u, 4);
                var crc = Crc32C.Update(uint.MaxValue, page.Array, 0, PAGE_SIZE);
                metadata.Write(~Crc32C.Update(crc, metadata.Array, 0, metadata.Count), 4);
                Assert.Throws<PageChecksumException>(() => checksum.Validate(page, metadata, 0));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FutureFormatCannotBeRolledBackByARecoveryJournal(bool extendedHeader)
        {
            using var source = new WalTestDatabase(null);
            source.Database.Checkpoint();
            var header = source.Data.ToArray();
            using var log = CreateJournal(header);
            header[HeaderPage.P_FILE_VERSION] = HeaderPage.CURRENT_FILE_VERSION + 1;
            if (extendedHeader) header[BasePage.P_PAGE_FORMAT] = PageChecksum.Extended;
            var page = new BufferSlice(header, 0, PAGE_SIZE);
            page.Write(PageChecksum.Compute(page), BasePage.P_TRANSACTION_ID);
            using var data = ChecksumTestFiles.Copy(header);
            var wal = log.ToArray();
            Action open = () => { using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }); };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.UNSUPPORTED_FILE_VERSION);
            data.ToArray().Should().Equal(header);
            log.ToArray().Should().Equal(wal);
        }

        [Fact]
        public void InvalidCoverageInRecoveryCopyCannotBePublishedDuringRepair()
        {
            using var source = new WalTestDatabase(null);
            source.Database.Checkpoint();
            var header = source.Data.ToArray();
            header[DataChecksumPolicy.CoveragePosition] = 0;
            PageChecksum.Write(new BufferSlice(header, 0, PAGE_SIZE));
            using var log = CreateJournal(header);
            header[400] ^= 1;
            using var data = ChecksumTestFiles.Copy(header);
            var wal = log.ToArray();
            Action open = () => { using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }); };
            open.Should().Throw<PageChecksumException>();
            data.ToArray().Should().Equal(header);
            log.ToArray().Should().Equal(wal);
        }

        private static MemoryStream CreateJournal(byte[] header)
        {
            var log = new MemoryStream();
            var checksums = new WalChecksum();
            checksums.Reset(header.Skip(WalChecksum.SaltPosition).Take(16).ToArray());
            HeaderJournal.Write(log, header, false, checksums);
            return log;
        }

        [Fact]
        public void CoverageMetadataFailsClosedEvenWithAValidHeaderCrc()
        {
            var page = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
            var header = new HeaderPage(page, 0) { LastPageID = 10 };
            header.UpdateBuffer();
            var policy = new DataChecksumPolicy();
            policy.InitializeMixed(10);
            policy.Write(page);
            PageChecksum.Write(page);
            policy.Load(page);
            page[DataChecksumPolicy.CoveragePosition] = 0;
            PageChecksum.Write(page);
            Assert.Throws<PageChecksumException>(() => policy.Load(page));
            page[DataChecksumPolicy.CoveragePosition] = DataChecksumPolicy.MixedMarker;
            page.Write(11u, DataChecksumPolicy.LegacyBoundaryPosition);
            PageChecksum.Write(page);
            Assert.Throws<PageChecksumException>(() => policy.Load(page));
        }
    }
}
