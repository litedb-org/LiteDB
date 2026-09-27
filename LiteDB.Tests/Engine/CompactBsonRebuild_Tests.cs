using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CompactBsonRebuild_Tests
    {
        [Theory]
        [InlineData(null, ConnectionType.Direct)]
        [InlineData("secret", ConnectionType.Direct)]
        [InlineData(null, ConnectionType.Shared)]
        [InlineData("secret", ConnectionType.Shared)]
        public void Bson_rebuild_persists_v11_without_schemas_and_reopens_without_conversion(
            string password, ConnectionType connectionType)
        {
            using var file = new TempFile();
            var connection = new ConnectionString
            {
                Filename = file.Filename, Password = password, Connection = connectionType,
                CompactStorage = CompactStorageMode.Compact
            };
            var expected = Enumerable.Range(1, 80).Select(CompactStorage_Tests.Document).ToArray();
            using (var db = new LiteDatabase(connection))
            {
                db.GetCollection("docs").Insert(expected);
                db.GetCollection("docs").EnsureIndex("property", "$.RepeatedPropertyName0");
                db.Checkpoint();
            }
            Inspect(file.Filename, password, HeaderPage.COMPACT_FILE_VERSION).Should().BeGreaterThan(0,
                "the fixture must exercise schema removal, not just BSON copying");

            using (var db = new LiteDatabase(connection))
            {
                var options = new RebuildOptions { Password = password, CompactStorage = CompactStorageMode.Legacy };
                db.Rebuild(options);
                options.GetErrorReport().Should().BeEmpty();
            }
            Inspect(file.Filename, password, HeaderPage.INDEX_FILE_VERSION).Should().Be(0);
            var rebuilt = File.ReadAllBytes(file.Filename);
            // Keep Auto enabled: opening must not silently repromote a BSON rebuild.
            connection.CompactStorage = CompactStorageMode.Auto;
            foreach (var readOnly in new[] { true, false, true })
            {
                connection.ReadOnly = readOnly;
                using (var db = new LiteDatabase(connection))
                {
                    var docs = db.GetCollection("docs");
                    docs.Count().Should().Be(expected.Length);
                    foreach (var doc in expected)
                    {
                        var id = doc["_id"].AsInt32;
                        BsonSerializer.Serialize(docs.FindById(id)).Should().Equal(BsonSerializer.Serialize(doc));
                        docs.Find(BsonExpression.Create("$.RepeatedPropertyName0 = @0", id)).Select(x => x["_id"].AsInt32)
                            .Should().Equal(id);
                    }
                }
                File.ReadAllBytes(file.Filename).Should().Equal(rebuilt);
                Inspect(file.Filename, password, HeaderPage.INDEX_FILE_VERSION).Should().Be(0);
            }
        }

        private static int Inspect(string path, string password, byte expectedVersion)
        {
            using var file = File.OpenRead(path);
            using var data = password == null ? (Stream)file : new AesStream(password, file, allowRecovery: false);
            var page = new byte[Constants.PAGE_SIZE];
            var schemas = 0;
            for (long offset = 0; offset < data.Length; offset += page.Length)
            {
                data.ReadRequired(page, 0, page.Length);
                if (offset == 0) page[HeaderPage.P_FILE_VERSION].Should().Be(expectedVersion);
                if (page[BasePage.P_PAGE_TYPE] == (byte)PageType.Schema) schemas++;
            }
            return schemas;
        }
    }
}
