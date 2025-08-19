using AwesomeAssertions;
using Xunit;

namespace LiteDB.Tests.Document
{
    public class Decimal_Tests
    {
        [Fact]
        public void BsonValue_New_Decimal_Type()
        {
            var d0 = 0m;
            var d1 = 1m;
            var dmin = new BsonValue(decimal.MinValue);
            var dmax = new BsonValue(decimal.MaxValue);

            JsonSerializer.Serialize(d0).Should().Be("{\"$numberDecimal\":\"0\"}");
            JsonSerializer.Serialize(d1).Should().Be("{\"$numberDecimal\":\"1\"}");
            JsonSerializer.Serialize(dmin).Should().Be("{\"$numberDecimal\":\"-79228162514264337593543950335\"}");
            JsonSerializer.Serialize(dmax).Should().Be("{\"$numberDecimal\":\"79228162514264337593543950335\"}");

            var b0 = BsonSerializer.Serialize(new BsonDocument {{"A", d0}});
            var b1 = BsonSerializer.Serialize(new BsonDocument {{"A", d1}});
            var bmin = BsonSerializer.Serialize(new BsonDocument {{"A", dmin}});
            var bmax = BsonSerializer.Serialize(new BsonDocument {{"A", dmax}});

            var x0 = BsonSerializer.Deserialize(b0);
            var x1 = BsonSerializer.Deserialize(b1);
            var xmin = BsonSerializer.Deserialize(bmin);
            var xmax = BsonSerializer.Deserialize(bmax);

            x0["A"].AsDecimal.Should().Be(d0);
            x1["A"].AsDecimal.Should().Be(d1);
            xmin["A"].AsDecimal.Should().Be(dmin.AsDecimal);
            xmax["A"].AsDecimal.Should().Be(dmax.AsDecimal);
        }
    }
}