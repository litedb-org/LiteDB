using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class PageChecksum_Tests
    {
        [Fact]
        public void CorruptedVersionByte_CannotDisableHeaderChecksums()
        {
            using var test = new WalTestDatabase(null);
            test.Seed("docs");
            var bytes = test.Data.ToArray();
            bytes[HeaderPage.P_FILE_VERSION] = HeaderPage.FILE_VERSION;
            using var data = ChecksumTestFiles.Copy(bytes);
            using var log = ChecksumTestFiles.Copy(test.Log.ToArray());
            Action open = () => { using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }); };
            open.Should().Throw<PageChecksumException>();
            data.ToArray().Should().Equal(bytes);
            log.ToArray().Should().Equal(test.Log.ToArray());
        }

        [Fact]
        public void Crc32C_MatchesPublishedCheckValueAndPortableImplementation()
        {
            var bytes = Encoding.ASCII.GetBytes("123456789");
            (~Crc32C.Update(uint.MaxValue, bytes, 0, bytes.Length)).Should().Be(0xE3069283u);
            (~Crc32C.UpdatePortable(uint.MaxValue, bytes, 0, bytes.Length)).Should().Be(0xE3069283u);
            var random = new Random(2935);
            bytes = new byte[PAGE_SIZE + 16];
            random.NextBytes(bytes);
            for (var offset = 0; offset < 8; offset++)
                for (var length = 0; length < bytes.Length - offset; length += 31)
                    Crc32C.Update(123u, bytes, offset, length).Should()
                        .Be(Crc32C.UpdatePortable(123u, bytes, offset, length));
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        public void CorruptDataPagesAreRejectedBeforeDeserialization(string password, bool header)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("docs");
            test.Database.Checkpoint();
            var bytes = test.Data.ToArray();
            var preamble = password == null ? 0 : PAGE_SIZE;
            var page = header ? 0 : Enumerable.Range(1, (bytes.Length - preamble) / PAGE_SIZE - 1).Last();
            bytes[preamble + page * PAGE_SIZE + 400] ^= 0x10;
            using var data = ChecksumTestFiles.Copy(bytes);
            using var log = new MemoryStream();
            Action read = () =>
            {
                using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.GetCollection("docs").FindAll().ToArray();
            };
            read.Should().Throw<PageChecksumException>();
            data.ToArray().Should().Equal(bytes);
        }

        [Theory]
        [InlineData(null, false, 8)]
        [InlineData("secret", false, 8)]
        [InlineData(null, true, 8)]
        [InlineData("secret", true, 8)]
        [InlineData(null, false, 9)]
        [InlineData("secret", false, 9)]
        [InlineData(null, true, 9)]
        [InlineData("secret", true, 9)]
        public void LegacyWalRecoversBeforeWritableConversion_WhileReadOnlyPreservesBytes(string password, bool readOnly, byte version)
        {
            using var test = new WalTestDatabase(password);
            test.Seed("docs");
            test.Database.Checkpoint();
            test.Update("docs", 1);
            using var data = ChecksumTestFiles.Copy(test.Data.ToArray());
            using var log = ChecksumTestFiles.Copy(test.Log.ToArray());
            ChecksumTestFiles.MakeLegacy(data, log, password, version);
            var originalData = data.ToArray();
            var originalLog = log.ToArray();
            if (readOnly)
            {
                Action open = () => { using var engine = new LiteEngine(new EngineSettings
                    { DataStream = data, LogStream = log, Password = password, ReadOnly = true }); };
                open.Should().Throw<LiteException>().WithMessage("*index ordering*requires migration*");
                data.ToArray().Should().Equal(originalData);
                log.ToArray().Should().Equal(originalLog);
                return;
            }
            using (var engine = new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly
            }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("docs").FindAll().Should().HaveCount(WalTestDatabase.DocumentCount)
                    .And.OnlyContain(x => x["value"].AsInt32 == 1);
                db.GetCollection("$database").FindAll().Single()["checksums"].AsBoolean.Should().Be(!readOnly);
                if (!readOnly)
                {
                    db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 100, ["value"] = 1 });
                    db.Checkpoint();
                }
            }
            if (readOnly)
            {
                data.ToArray().Should().Equal(originalData);
                log.ToArray().Should().Equal(originalLog);
            }
            else
            {
                using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
                using var reopened = new LiteDatabase(engine, disposeOnClose: false);
                reopened.GetCollection("docs").Count().Should().Be(WalTestDatabase.DocumentCount + 1);
            }
        }
    }
}
