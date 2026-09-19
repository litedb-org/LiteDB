using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class RebuildEncryption_Tests
    {
        [Theory]
        [InlineData(ConnectionType.Direct, "omitted")]
        [InlineData(ConnectionType.Shared, "omitted")]
        [InlineData(ConnectionType.Direct, "sql")]
        [InlineData(ConnectionType.Shared, "sql")]
        [InlineData(ConnectionType.Direct, "change")]
        [InlineData(ConnectionType.Shared, "change")]
        [InlineData(ConnectionType.Direct, "remove")]
        [InlineData(ConnectionType.Shared, "remove")]
        public void Encryption_rebuild_preserves_case_sensitive_keys_and_collation(ConnectionType connection, string operation)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-rebuild-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "data.db");
            var collation = new Collation("en-US/None");
            var password = operation == "change" ? "new" : operation == "remove" ? null : "old";
            try
            {
                using (var db = new LiteDatabase(new ConnectionString
                {
                    Filename = path, Password = "old", Collation = collation, Connection = connection
                }))
                {
                    var rows = db.GetCollection("rows");
                    rows.Insert(new BsonDocument { ["_id"] = "ana", ["value"] = 1 });
                    rows.Insert(new BsonDocument { ["_id"] = "ANA", ["value"] = 2 });
                    rows.EnsureIndex("value", true);
                    if (operation == "omitted") db.Rebuild();
                    else if (operation == "sql")
                    {
                        using var reader = db.Execute("REBUILD");
                        reader.ToArray();
                    }
                    else
                    {
                        var options = new RebuildOptions { Password = password, RemovePassword = operation == "remove" };
                        db.Rebuild(options);
                        options.Collation.Should().BeNull("rebuilding must not mutate caller options");
                        options.GetErrorReport().Should().BeEmpty();
                    }
                    db.Collation.ToString().Should().Be(collation.ToString());
                    rows.FindById("ana")["value"].AsInt32.Should().Be(1);
                    rows.FindById("ANA")["value"].AsInt32.Should().Be(2);
                    rows.Insert(new BsonDocument { ["_id"] = "next", ["value"] = 3 });
                }
                using var reopened = new LiteDatabase(new ConnectionString { Filename = path, Password = password });
                reopened.Collation.ToString().Should().Be(collation.ToString());
                reopened.GetCollection("rows").Count().Should().Be(3);
                reopened.GetCollection("rows").FindById("ana")["value"].AsInt32.Should().Be(1);
                reopened.GetCollection("rows").FindById("ANA")["value"].AsInt32.Should().Be(2);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
