using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2549StreamCheckpoint_Tests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Rebuild_publishes_stream_and_preserves_wal_ownership_and_encryption(string password)
        {
            using var data = new MemoryStream();
            using var wal = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = wal, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "one" });
            db.Rebuild().Should().Be(0);
            AssertSnapshot(data, password, 1);
            data.CanWrite.Should().BeTrue();
            wal.CanWrite.Should().BeTrue();
            rows.Insert(new BsonDocument { ["_id"] = 2, ["value"] = "two" });
            db.Rebuild().Should().Be(0);
            AssertSnapshot(data, password, 2);
            rows.Count().Should().Be(2);
        }

        [Fact]
        public void Stream_rebuild_rejects_active_transaction_without_committing_it()
        {
            using var data = new MemoryStream();
            using var db = new LiteDatabase(data);
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });
            db.Rebuild();
            db.BeginTrans().Should().BeTrue();
            rows.Insert(new BsonDocument { ["_id"] = 2 });
            Action rebuild = () => db.Rebuild();
            rebuild.Should().Throw<LiteException>().WithMessage("*transactions are active*");
            AssertSnapshot(data, null, 1);
            db.Rollback().Should().BeTrue();
            rows.Count().Should().Be(1);
            db.Rebuild().Should().Be(0);
        }

        private static void AssertSnapshot(MemoryStream data, string password, int count)
        {
            using var copy = new MemoryStream(data.ToArray());
            using var engine = new LiteEngine(new EngineSettings { DataStream = copy, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("rows").Count().Should().Be(count);
        }
    }
}
