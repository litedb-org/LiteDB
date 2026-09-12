using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2819_Tests
    {
        [Theory]
        [InlineData(255, false)]
        [InlineData(256, false)]
        [InlineData(300, false)]
        [InlineData(1000, false)]
        [InlineData(255, true)]
        [InlineData(256, true)]
        [InlineData(1000, true)]
        public void Extended_sort_keys_preserve_order_and_payload_with_and_without_index(int length, bool binary)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = Collation.Binary });
            var col = db.GetCollection("rows");
            BsonValue key = binary ? new BsonValue(Enumerable.Repeat((byte)'A', length).ToArray()) : new BsonValue(new string('A', length));
            BsonValue shortA = binary ? new BsonValue(new[] { (byte)'A' }) : new BsonValue("A");
            BsonValue shortB = binary ? new BsonValue(new[] { (byte)'B' }) : new BsonValue("B");
            col.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["key"] = shortB, ["payload"] = "b" },
                new BsonDocument { ["_id"] = 2, ["key"] = key, ["payload"] = "long" },
                new BsonDocument { ["_id"] = 3, ["key"] = shortA, ["payload"] = "a" }
            });
            foreach (var indexed in new[] { false, true })
            {
                if (indexed) col.EnsureIndex("key");
                var ascending = col.Query().OrderBy("key").ToArray();
                ascending.Select(x => x["_id"].AsInt32).Should().Equal(3, 2, 1);
                ascending.Select(x => x["payload"].AsString).Should().Equal("a", "long", "b");
                ascending[1]["key"].Should().Be(key);
                col.Query().OrderByDescending("key").ToArray().Select(x => x["_id"].AsInt32).Should().Equal(1, 2, 3);
            }
        }
    }
}
