using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2819_Tests
    {
        private const int ExtendedKeyBytes = 300;
        private const int MaximumSerializedKeyBytes = 1023;
        private const int MaximumPayloadBytes = MaximumSerializedKeyBytes - 2;

        [Theory]
        [InlineData(255, false)]
        [InlineData(256, false)]
        [InlineData(300, false)]
        [InlineData(1000, false)]
        [InlineData(255, true)]
        [InlineData(256, true)]
        [InlineData(300, true)]
        [InlineData(1000, true)]
        public void Extended_unindexed_sort_keys_preserve_the_entire_key(int length, bool binary)
        {
            using var db = OpenMemoryDatabase();
            var col = db.GetCollection("rows");
            var low = CreateKey(1, binary, length);
            var high = CreateKey(2, binary, length);

            col.Insert(new[]
            {
                Row(1, high, "high"),
                Row(2, low, "low")
            });

            var ascending = col.Query().OrderBy("key").ToArray();
            ascending.Select(x => x["_id"].AsInt32).Should().Equal(2, 1);
            ascending.Select(x => x["payload"].AsString).Should().Equal("low", "high");
            ascending[0]["key"].Should().Be(low);
            ascending[1]["key"].Should().Be(high);

            var descending = col.Query().OrderByDescending("key").ToArray();
            descending.Select(x => x["_id"].AsInt32).Should().Equal(1, 2);
            descending[0]["key"].Should().Be(high);
            descending[1]["key"].Should().Be(low);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Maximum_legal_unindexed_key_preserves_its_last_bytes(bool binary)
        {
            using var db = OpenMemoryDatabase();
            var col = db.GetCollection("rows");
            var low = CreateKey(1, binary, MaximumPayloadBytes);
            var high = CreateKey(2, binary, MaximumPayloadBytes);

            Constants.MAX_INDEX_KEY_LENGTH.Should().Be(MaximumSerializedKeyBytes);

            col.Insert(new[]
            {
                Row(1, high, "maximum-high"),
                Row(2, low, "maximum-low")
            });

            var actual = col.Query().OrderBy("key").ToArray();
            actual.Select(x => x["_id"].AsInt32).Should().Equal(2, 1);
            actual.Select(x => x["payload"].AsString).Should().Equal("maximum-low", "maximum-high");
            actual[0]["key"].Should().Be(low);
            actual[1]["key"].Should().Be(high);
            GetPayloadLength(actual[0]["key"]).Should().Be(MaximumPayloadBytes);
            IndexNode.GetKeyLength(actual[0]["key"], true).Should().Be(MaximumSerializedKeyBytes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Clearly_oversized_unindexed_key_reports_the_sort_key_contract(bool binary)
        {
            using var db = OpenMemoryDatabase();
            var col = db.GetCollection("rows");
            var oversized = CreateKey(1, binary, 1100);

            col.Insert(new[]
            {
                Row(1, oversized, "oversized"),
                Row(2, CreateKey(2, binary, ExtendedKeyBytes), "legal")
            });

            Action sort = () => col.Query().OrderBy("key").ToArray();
            var failure = sort.Should().Throw<LiteException>().Which;
            failure.ErrorCode.Should().Be(LiteException.INVALID_INDEX_KEY);
            failure.Message.Should().Be($"Sort key must be less than {MaximumSerializedKeyBytes} bytes.");

            col.Count().Should().Be(2);
            col.FindById(1)["key"].Should().Be(oversized);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Extended_indexed_key_uses_the_named_index_in_an_independent_control(bool binary)
        {
            using var db = OpenMemoryDatabase();
            var col = db.GetCollection("rows");
            var low = CreateKey(1, binary, ExtendedKeyBytes);
            var high = CreateKey(2, binary, ExtendedKeyBytes);

            col.Insert(new[]
            {
                Row(1, high, "indexed-high"),
                Row(2, low, "indexed-low")
            });
            col.EnsureIndex("key").Should().BeTrue();

            var plan = col.Query().OrderBy("key").GetPlan();
            plan["index"]["name"].AsString.Should().Be("key");
            plan["index"]["expr"].AsString.Should().Be("$.key");
            plan["index"]["order"].AsInt32.Should().Be(Query.Ascending);

            var actual = col.Query().OrderBy("key").ToArray();
            actual.Select(x => x["_id"].AsInt32).Should().Equal(2, 1);
            actual.Select(x => x["payload"].AsString).Should().Equal("indexed-low", "indexed-high");
            actual[0]["key"].Should().Be(low);
            actual[1]["key"].Should().Be(high);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Unindexed_extended_keys_are_merged_from_multiple_sort_containers(bool binary)
        {
            const int rowCount = 2800;
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            using var temp = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = data,
                LogStream = log,
                TempStream = temp,
                Collation = Collation.Binary
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var col = db.GetCollection("rows");
            var serializedEntryBytes = IndexNode.GetKeyLength(
                CreateKey(1, binary, ExtendedKeyBytes), true) + PageAddress.SIZE;

            ((long)rowCount * serializedEntryBytes)
                .Should().BeGreaterThan(Constants.CONTAINER_SORT_SIZE);

            col.InsertBulk(Enumerable.Range(1, rowCount).Select(id =>
            {
                var rank = rowCount - id + 1;
                return new BsonDocument
                {
                    ["_id"] = id,
                    ["rank"] = rank,
                    ["key"] = CreateKey(rank, binary, ExtendedKeyBytes),
                    ["payload"] = Payload(id, rank)
                };
            })).Should().Be(rowCount);

            var plan = col.Query().OrderBy("key").GetPlan();
            plan["index"]["name"].AsString.Should().Be("_id");
            plan["orderBy"].AsArray.Single()["expr"].AsString.Should().Be("$.key");
            temp.Length.Should().Be(0);

            var actual = col.Query().OrderBy("key").ToArray();
            actual.Should().HaveCount(rowCount);
            for (var index = 0; index < rowCount; index++)
            {
                var rank = index + 1;
                var id = rowCount - rank + 1;
                actual[index]["_id"].AsInt32.Should().Be(id);
                actual[index]["rank"].AsInt32.Should().Be(rank);
                actual[index]["payload"].AsString.Should().Be(Payload(id, rank));
                actual[index]["key"].Should().Be(CreateKey(rank, binary, ExtendedKeyBytes));
            }

            temp.Length.Should().BeGreaterOrEqualTo(2L * Constants.CONTAINER_SORT_SIZE);
        }

        private static LiteDatabase OpenMemoryDatabase()
        {
            return new LiteDatabase(new ConnectionString
            {
                Filename = ":memory:",
                Collation = Collation.Binary
            });
        }

        private static BsonDocument Row(int id, BsonValue key, string payload)
        {
            return new BsonDocument { ["_id"] = id, ["key"] = key, ["payload"] = payload };
        }

        private static BsonValue CreateKey(int rank, bool binary, int payloadBytes)
        {
            var suffix = rank.ToString("D4");
            var text = new string('K', payloadBytes - suffix.Length) + suffix;
            return binary ? new BsonValue(Encoding.ASCII.GetBytes(text)) : new BsonValue(text);
        }

        private static int GetPayloadLength(BsonValue key)
        {
            return key.IsBinary ? key.AsBinary.Length : Encoding.UTF8.GetByteCount(key.AsString);
        }

        private static string Payload(int id, int rank)
        {
            return $"id={id:D4};rank={rank:D4}";
        }
    }
}
