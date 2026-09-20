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
            var rebuiltBytes = stream.ToArray();
            using var copy = new MemoryStream(rebuiltBytes);
            using var reopened = new LiteDatabase(copy);
            var rows = reopened.GetCollection("rows").FindAll().ToArray();
            rows.Should().ContainSingle();
            rows[0]["_id"].AsInt32.Should().Be(17);
            rows[0]["value"].AsString.Should().Be("before rebuild");

            col.Insert(new BsonDocument { ["_id"] = 18, ["value"] = "after rebuild" });
            col.Count().Should().Be(2);
            database.Checkpoint();

            var postRebuildWriteBytes = stream.ToArray();
            postRebuildWriteBytes.Should().NotEqual(rebuiltBytes,
                "writes after rebuild must continue targeting the caller-owned stream");
            using var postRebuildCopy = new MemoryStream(postRebuildWriteBytes);
            using var postRebuildReopen = new LiteDatabase(postRebuildCopy);
            var postRebuildRows = postRebuildReopen.GetCollection("rows").FindAll()
                .OrderBy(x => x["_id"].AsInt32)
                .ToArray();
            postRebuildRows.Select(x => x["_id"].AsInt32).Should().Equal(17, 18);
            postRebuildRows.Select(x => x["value"].AsString)
                .Should().Equal("before rebuild", "after rebuild");
        }
    }
}
