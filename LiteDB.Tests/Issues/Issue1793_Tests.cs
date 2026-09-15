using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1793_Tests
    {
        [Theory]
        [InlineData("2147483648", 2147483648L)]
        [InlineData("-2147483649", -2147483649L)]
        [InlineData("210000000000", 210000000000L)]
        [InlineData("-210000000000", -210000000000L)]
        [InlineData("9007199254740993", 9007199254740993L)]
        [InlineData("-9007199254740993", -9007199254740993L)]
        [InlineData("9223372036854775807", long.MaxValue)]
        [InlineData("-9223372036854775808", long.MinValue)]
        public void Large_integral_tokens_are_exact_Int64_values_and_round_trip(string token, long expected)
        {
            var json = "{\"before\":\"kept\",\"large\":" + token + ",\"after\":17}";

            var document = JsonSerializer.Deserialize(json).AsDocument;

            Assert.NotNull(document);
            document.Count.Should().Be(3);
            document["before"].RawValue.Should().BeOfType<string>().Which.Should().Be("kept");
            document["after"].RawValue.Should().BeOfType<int>().Which.Should().Be(17);
            document["large"].Type.Should().Be(BsonType.Int64);
            document["large"].RawValue.Should().BeOfType<long>().Which.Should().Be(expected);
            document["large"].AsInt64.Should().Be(expected);

            var serialized = JsonSerializer.Serialize(document);
            serialized.Should().Contain("\"$numberLong\":\"" + token + "\"");

            var roundTrip = JsonSerializer.Deserialize(serialized).AsDocument;
            Assert.NotNull(roundTrip);
            roundTrip.Count.Should().Be(3);
            roundTrip["before"].AsString.Should().Be("kept");
            roundTrip["after"].AsInt32.Should().Be(17);
            roundTrip["large"].Type.Should().Be(BsonType.Int64);
            roundTrip["large"].RawValue.Should().BeOfType<long>().Which.Should().Be(expected);
        }

        [Theory]
        [InlineData("2147483647", int.MaxValue)]
        [InlineData("-2147483648", int.MinValue)]
        public void Int32_boundary_tokens_remain_Int32(string token, int expected)
        {
            var value = JsonSerializer.Deserialize(token);

            value.Type.Should().Be(BsonType.Int32);
            value.RawValue.Should().BeOfType<int>().Which.Should().Be(expected);
            value.AsInt32.Should().Be(expected);
        }

        [Fact]
        public void Numeric_syntax_distinguishes_bare_Int64_from_explicit_Double()
        {
            var document = JsonSerializer.Deserialize(
                "{\"bare\":210000000000,\"explicitDouble\":210000000000.0}").AsDocument;

            document["bare"].Type.Should().Be(BsonType.Int64);
            document["bare"].RawValue.Should().BeOfType<long>().Which.Should().Be(210000000000L);
            document["explicitDouble"].Type.Should().Be(BsonType.Double);
            document["explicitDouble"].RawValue.Should().BeOfType<double>().Which.Should().Be(210000000000D);
        }
    }
}
