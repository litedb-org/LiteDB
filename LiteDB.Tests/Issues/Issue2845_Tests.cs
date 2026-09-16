using System;
using System.Collections.Generic;
using System.Globalization;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2845_Tests
    {
        [Theory]
        [InlineData("-0.0")]
        [InlineData("-0E+20")]
        [InlineData("-0.000e-20")]
        public void Json_reader_preserves_explicit_negative_zero(string json)
        {
            var value = JsonSerializer.Deserialize(json);
            value.Type.Should().Be(BsonType.Double);
            BitConverter.DoubleToInt64Bits(value.AsDouble).Should().Be(long.MinValue);
        }

        [Fact]
        public void Json_round_trips_sampled_finite_bits_under_a_comma_decimal_culture()
        {
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var random = new Random(2845);
                var bytes = new byte[8];
                for (var i = 0; i < 2000; i++)
                {
                    random.NextBytes(bytes);
                    var value = BitConverter.ToDouble(bytes, 0);
                    if (double.IsNaN(value) || double.IsInfinity(value)) continue;

                    var json = JsonSerializer.Serialize(new BsonValue(value));
                    var parsed = double.Parse(json, CultureInfo.InvariantCulture);
                    BitConverter.DoubleToInt64Bits(parsed).Should().Be(BitConverter.DoubleToInt64Bits(value));
                    var bson = JsonSerializer.Deserialize(json);
                    bson.Type.Should().Be(BsonType.Double);
                    BitConverter.DoubleToInt64Bits(bson.AsDouble).Should().Be(BitConverter.DoubleToInt64Bits(value));
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void Non_finite_values_keep_the_existing_json_null_representation(double value)
        {
            JsonSerializer.Serialize(new BsonValue(value)).Should().Be("null");
        }

        public static IEnumerable<object[]> FiniteDoubles()
        {
            foreach (var value in new[] { double.MaxValue, double.MinValue, double.Epsilon,
                -double.Epsilon, 0.1 + 0.2, 1e-300, 1.2345678901234567, 42.0,
                0.0, BitConverter.Int64BitsToDouble(long.MinValue),
                BitConverter.Int64BitsToDouble(0x0010000000000000),
                BitConverter.Int64BitsToDouble(0x000fffffffffffff),
                9007199254740992.0, 1e20, -1e20 })
            {
                yield return new object[] { value };
            }
        }

        [Theory]
        [MemberData(nameof(FiniteDoubles))]
        public void Json_preserves_double_type_and_bits_through_repeated_exports(double value)
        {
            var original = new BsonDocument { ["Value"] = value, ["Sentinel"] = "untouched" };
            var binary = BsonSerializer.Deserialize(BsonSerializer.Serialize(original));
            BitConverter.DoubleToInt64Bits(binary["Value"].AsDouble)
                .Should().Be(BitConverter.DoubleToInt64Bits(value));

            var current = original;
            for (var pass = 0; pass < 3; pass++)
            {
                current = JsonSerializer.Deserialize(JsonSerializer.Serialize(current)).AsDocument;
                current["Value"].Type.Should().Be(BsonType.Double);
                // Numeric equality alone can hide rounding and changes in representation.
                BitConverter.DoubleToInt64Bits(current["Value"].AsDouble)
                    .Should().Be(BitConverter.DoubleToInt64Bits(value));
                current["Sentinel"].AsString.Should().Be("untouched");
            }
        }
    }
}
