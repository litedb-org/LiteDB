using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class LazyChecksumMigration_Tests
    {
        [Theory]
        [InlineData(null, 8)]
        [InlineData("secret", 8)]
        [InlineData(null, 9)]
        [InlineData("secret", 9)]
        public void CutoverChangesOnlyHeader_ThenWritesMigratePagesOutOfOrder(string password, byte version)
        {
            using var source = new WalTestDatabase(password);
            source.Seed("cold");
            source.Seed("hot");
            source.Database.GetCollection("hot").EnsureIndex("value");
            source.Database.Checkpoint();
            using var data = ChecksumTestFiles.Copy(source.Data.ToArray());
            using var log = new MemoryStream();
            ChecksumTestFiles.MakeLegacy(data, log, password, version);
            var before = data.ToArray();
            var settings = new EngineSettings { DataStream = data, LogStream = log, Password = password };
            uint boundary;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                data.ToArray().Skip(password == null ? PAGE_SIZE : 2 * PAGE_SIZE)
                    .Should().Equal(before.Skip(password == null ? PAGE_SIZE : 2 * PAGE_SIZE));
                var status = db.GetCollection("$database").FindAll().Single();
                status["checksumCoverage"].AsString.Should().Be("Mixed");
                boundary = (uint)status["legacyLastPageID"].AsInt64;
                db.GetCollection("cold").Count().Should().Be(WalTestDatabase.DocumentCount);
                // Change pages late in the legacy range while early cold pages remain untouched.
                db.GetCollection("hot").UpdateMany("{ value: 42 }", "true");
                db.GetCollection("new").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = new string('n', 40000) });
                db.Checkpoint();
                db.GetCollection("hot").Find(Query.EQ("value", 42)).Should().HaveCount(WalTestDatabase.DocumentCount);
            }
            var pages = ReadPages(data, password);
            pages.Skip(1).Take((int)boundary).Should().Contain(p => p[BasePage.P_PAGE_FORMAT] == PageChecksum.Legacy)
                .And.Contain(p => p[BasePage.P_PAGE_FORMAT] == PageChecksum.Checksummed);
            pages.Skip((int)boundary + 1).Should().NotBeEmpty()
                .And.OnlyContain(p => p[BasePage.P_PAGE_FORMAT] == PageChecksum.Checksummed);
            foreach (var page in pages.Where(p => p[BasePage.P_PAGE_FORMAT] == PageChecksum.Checksummed))
                PageChecksum.Validate(new BufferSlice(page, 0, PAGE_SIZE), BitConverter.ToUInt32(page, 0) * (long)PAGE_SIZE);
            var unchangedData = data.ToArray();
            var unchangedLog = log.ToArray();
            settings.ReadOnly = true;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("cold").FindAll().Should().HaveCount(WalTestDatabase.DocumentCount);
                db.GetCollection("hot").Find(Query.EQ("value", 42)).Should().HaveCount(WalTestDatabase.DocumentCount);
                db.GetCollection("new").FindById(1)["payload"].AsString.Should().HaveLength(40000);
            }
            var errors = new List<FileReaderError>();
            using (var reader = new FileReaderV8(settings, errors))
            {
                reader.Open();
                reader.GetDocuments("cold").Should().HaveCount(WalTestDatabase.DocumentCount);
                reader.GetDocuments("hot").Should().HaveCount(WalTestDatabase.DocumentCount);
                errors.Should().BeEmpty();
            }
            data.ToArray().Should().Equal(unchangedData);
            log.ToArray().Should().Equal(unchangedLog);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void ExplicitRebuildCompletesMixedCoverage(string password)
        {
            using var source = new WalTestDatabase(password);
            source.Seed("docs");
            source.Database.Checkpoint();
            using var data = ChecksumTestFiles.Copy(source.Data.ToArray());
            using var log = new MemoryStream();
            ChecksumTestFiles.MakeLegacy(data, log, password);
            var filename = Path.Combine(Path.GetTempPath(), "litedb-lazy-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                File.WriteAllBytes(filename, data.ToArray());
                using (var db = new LiteDatabase(new ConnectionString { Filename = filename, Password = password }))
                {
                    db.GetCollection("$database").FindAll().Single()["checksumCoverage"].AsString.Should().Be("Mixed");
                    db.Rebuild(new RebuildOptions { Password = password });
                    db.GetCollection("$database").FindAll().Single()["checksumCoverage"].AsString.Should().Be("Complete");
                    db.GetCollection("docs").Count().Should().Be(WalTestDatabase.DocumentCount);
                    db.Checkpoint();
                }
                using var rebuilt = ChecksumTestFiles.Copy(File.ReadAllBytes(filename));
                ReadPages(rebuilt, password).Should().OnlyContain(p => p[BasePage.P_PAGE_FORMAT] == PageChecksum.Checksummed);
                using var reopened = new LiteDatabase(new ConnectionString { Filename = filename, Password = password });
                reopened.GetCollection("docs").Count().Should().Be(WalTestDatabase.DocumentCount);
                reopened.GetCollection("$database").FindAll().Single()["legacyLastPageID"].AsInt64.Should().Be(0);
            }
            finally
            {
                File.Delete(filename);
                File.Delete(FileHelper.GetLogFile(filename));
                File.Delete(Path.ChangeExtension(filename, null) + "-backup.db");
            }
        }

        internal static byte[][] ReadPages(Stream data, string password)
        {
            using var factory = new StreamFactory(data, password);
            using var stream = factory.GetStream(false, false);
            var pages = new List<byte[]>();
            for (long position = 0; position < stream.Length; position += PAGE_SIZE)
            {
                var page = new byte[PAGE_SIZE];
                stream.ReadRequired(page, 0, page.Length);
                pages.Add(page);
            }
            return pages.ToArray();
        }
    }
}
