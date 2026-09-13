using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2816_Tests
    {
        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Read_then_dispose_with_foreign_log_handle_releases_data_handle(ConnectionType mode)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-2816-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "data.db");
            var log = Path.Combine(directory, "data-log.db");
            try
            {
                using (var db = new LiteDatabase(file))
                {
                    db.CheckpointSize = 0;
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "in WAL" });
                }
                File.Exists(log).Should().BeTrue();
                using (var foreign = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var db = new LiteDatabase(new ConnectionString { Filename = file, Connection = mode });
                    try { db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("in WAL"); }
                    finally { db.Dispose(); }
                }
                // No GC/finalizer: ownership must end synchronously at Dispose.
                using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                using (var db = new LiteDatabase(file))
                {
                    db.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("in WAL");
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                    db.GetCollection("rows").Count().Should().Be(2);
                }
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
