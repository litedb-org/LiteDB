using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class ChecksumIntegrityBoundary_Tests
    {
        [Fact]
        public void ValidDataPageChecksum_DoesNotPermitAMisdirectedPage()
        {
            var bytes = new byte[PAGE_SIZE];
            new Random(2935).NextBytes(bytes);
            var page = new BufferSlice(bytes, 0, bytes.Length);
            page.Write(7u, BasePage.P_PAGE_ID);
            PageChecksum.Write(page);
            PageChecksum.Validate(page, 7L * PAGE_SIZE);
            Action read = () => PageChecksum.Validate(page, 8L * PAGE_SIZE);
            read.Should().Throw<PageChecksumException>().WithMessage("*Data*65536*");
        }

        [Fact]
        public void EveryByteOfDataPageAndWalTrailer_IsCovered()
        {
            var bytes = new byte[PAGE_SIZE];
            new Random(2935).NextBytes(bytes);
            var page = new BufferSlice(bytes, 0, bytes.Length);
            page.Write(7u, BasePage.P_PAGE_ID);
            page.Write(false, BasePage.P_IS_CONFIRMED);
            PageChecksum.Write(page);
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] ^= 1;
                Assert.Throws<PageChecksumException>(() => PageChecksum.Validate(page, 7L * PAGE_SIZE));
                bytes[index] ^= 1;
            }

            var checksums = new WalChecksum();
            checksums.Reset(Guid.NewGuid().ToByteArray());
            var trailer = new BufferSlice(new byte[WalChecksum.MetadataSize], 0, WalChecksum.MetadataSize);
            checksums.Prepare(page, trailer, 0);
            checksums.Validate(page, trailer, 0);
            for (var index = 0; index < trailer.Count; index++)
            {
                trailer[index] ^= 1;
                Assert.Throws<PageChecksumException>(() => checksums.Validate(page, trailer, 0));
                trailer[index] ^= 1;
            }
        }

        [Fact]
        public void AutomaticConversion_PreservesUnusedPreallocation()
        {
            using var original = new WalTestDatabase(null);
            original.Seed("docs");
            original.Database.Checkpoint();
            using var data = ChecksumTestFiles.Copy(original.Data.ToArray());
            using var log = new MemoryStream();
            ChecksumTestFiles.MakeLegacy(data, log, null);
            var allocated = checked((int)data.Length);
            data.SetLength(allocated + 64 * PAGE_SIZE);
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                data.ToArray().Skip(allocated).Should().OnlyContain(x => x == 0);
                db.GetCollection("docs").Count().Should().Be(WalTestDatabase.DocumentCount);
                db.GetCollection("new_pages").Insert(new BsonDocument { ["_id"] = 1, ["value"] = new string('y', PAGE_SIZE * 2) });
                db.Checkpoint();
            }
            using var reopenedEngine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            using var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false);
            reopened.GetCollection("new_pages").FindById(1)["value"].AsString.Should().HaveLength(PAGE_SIZE * 2);
        }
    }
}
