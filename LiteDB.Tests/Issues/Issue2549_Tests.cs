using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2549_Tests
    {
        [Fact]
        public void Rebuild_publishes_caller_owned_stream_before_database_disposal()
        {
            using var stream = new MemoryStream();
            using var database = new LiteDatabase(stream);
            var col = database.GetCollection("rows");
            col.Insert(new BsonDocument { ["_id"] = 17, ["value"] = "before rebuild" });
            database.Rebuild();
            stream.CanRead.Should().BeTrue("the stream remains caller-owned");
            using var copy = new MemoryStream(stream.ToArray());
            using var reopened = new LiteDatabase(copy);
            var rows = reopened.GetCollection("rows").FindAll().ToArray();
            rows.Should().ContainSingle();
            rows[0]["_id"].AsInt32.Should().Be(17);
            rows[0]["value"].AsString.Should().Be("before rebuild");
            col.Insert(new BsonDocument { ["_id"] = 18, ["value"] = "after rebuild" });
            col.Count().Should().Be(2);
        }
    }
}
