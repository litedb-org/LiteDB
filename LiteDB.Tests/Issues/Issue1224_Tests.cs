using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1224_Tests
    {
        [Theory]
        [InlineData(2147483648L)]
        [InlineData(9007199254740993L)]
        [InlineData(long.MaxValue)]
        public void Unsigned_keys_within_Int64_range_retain_every_bit(long signed)
        {
            BsonValue key = (ulong)signed;
            key.Type.Should().Be(BsonType.Int64);
            key.AsInt64.Should().Be(signed);
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection("keys");
            // Write signed keys, look up unsigned keys. Do not round-trip through the same conversion.
            col.Insert(new BsonDocument { ["_id"] = signed, ["Value"] = "target" });
            col.Insert(new BsonDocument { ["_id"] = signed - 1, ["Value"] = "neighbor" });
            col.FindById(key)["Value"].AsString.Should().Be("target");
            col.FindById((ulong)(signed - 1))["Value"].AsString.Should().Be("neighbor");
            col.Count().Should().Be(2);
        }
    }
}
