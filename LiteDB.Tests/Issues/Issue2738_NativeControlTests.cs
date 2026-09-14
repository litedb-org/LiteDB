using System;
using System.IO;
using System.Linq;

using FluentAssertions;

using Xunit;

namespace LiteDB.Tests.Issues
{
    // A portable creation/lifecycle control. Passing on a desktop runtime does not
    // resolve the Android report: its storage path and original file state are unknown.
    [Trait("IssueStatus", "UnconfirmedPlatform")]
    public class Issue2738_NativeControlTests
    {
        [Theory]
        [InlineData(ConnectionType.Direct, false)]
        [InlineData(ConnectionType.Shared, false)]
        [InlineData(ConnectionType.Direct, true)]
        [InlineData(ConnectionType.Shared, true)]
        public void Fresh_encrypted_file_preserves_acknowledged_changes_across_reopens(
            ConnectionType connection, bool startWithEmptyFile)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-2738-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "created.db");
            var settings = new ConnectionString
            {
                Filename = path,
                Password = "issue-2738-control-password",
                Connection = connection
            };

            try
            {
                File.Exists(path).Should().BeFalse();
                if (startWithEmptyFile)
                {
                    File.WriteAllBytes(path, new byte[0]);
                    new FileInfo(path).Length.Should().Be(0);
                }

                using (var database = new LiteDatabase(settings))
                {
                    var rows = database.GetCollection("rows");
                    rows.InsertBulk(Enumerable.Range(1, 17).Select(id => ExpectedRow(id, 1)))
                        .Should().Be(17, "Shared mode must actually open and write the engine");
                    rows.EnsureIndex("token", true).Should().BeTrue();
                    database.UserVersion = 2738;
                    database.Checkpoint();
                }

                AssertEncryptedPrefix(path);
                using (var database = new LiteDatabase(settings))
                {
                    AssertLedger(database, updated: false);
                    var rows = database.GetCollection("rows");
                    rows.Update(ExpectedRow(4, 2)).Should().BeTrue();
                    rows.Delete(17).Should().BeTrue();
                    database.Checkpoint();
                }

                AssertEncryptedPrefix(path);
                for (var reopen = 0; reopen < 2; reopen++)
                {
                    using var database = new LiteDatabase(settings);
                    AssertLedger(database, updated: true);
                    database.Checkpoint();
                }
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static void AssertLedger(LiteDatabase database, bool updated)
        {
            database.UserVersion.Should().Be(2738);
            var rows = database.GetCollection("rows");
            var expectedIds = Enumerable.Range(1, updated ? 16 : 17).ToArray();
            var scanned = rows.FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
            scanned.Select(row => row["_id"].AsInt32).Should().Equal(expectedIds);
            rows.Count().Should().Be(expectedIds.Length);
            for (var index = 0; index < expectedIds.Length; index++)
            {
                var id = expectedIds[index];
                var expected = ExpectedRow(id, updated && id == 4 ? 2 : 1);
                AssertRow(scanned[index], expected);
                AssertRow(rows.FindById(id), expected);
                var indexed = rows.Find(Query.EQ("token", expected["token"])).ToArray();
                indexed.Should().ContainSingle();
                AssertRow(indexed[0], expected);
            }

            if (updated)
            {
                Assert.Null(rows.FindById(17));
                rows.Find(Query.EQ("token", "request-17")).Should().BeEmpty();
            }
        }

        private static void AssertRow(BsonDocument actual, BsonDocument expected)
        {
            Assert.NotNull(actual);
            actual.Keys.OrderBy(key => key).Should().Equal(expected.Keys.OrderBy(key => key));
            actual["_id"].Should().Be(expected["_id"]);
            actual["token"].Should().Be(expected["token"]);
            actual["generation"].Should().Be(expected["generation"]);
            actual["payload"].AsBinary.Should().Equal(expected["payload"].AsBinary);
        }

        private static BsonDocument ExpectedRow(int id, int generation)
        {
            return new BsonDocument
            {
                ["_id"] = id,
                ["token"] = "request-" + id,
                ["generation"] = generation,
                ["payload"] = Enumerable.Range(0, 257)
                    .Select(offset => (byte)((id * 17 + generation * 31 + offset * 13) % 251)).ToArray()
            };
        }

        private static void AssertEncryptedPrefix(string path)
        {
            using var stream = File.OpenRead(path);
            stream.Length.Should().BeGreaterThan(8192);
            stream.ReadByte().Should().Be(1, "a plaintext replacement must not satisfy the lifecycle control");
        }
    }
}
