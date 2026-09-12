using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2845_Tests
    {
        public static IEnumerable<object[]> FiniteDoubles()
        {
            foreach (var value in new[] { double.MaxValue, double.MinValue, double.Epsilon,
                -double.Epsilon, 0.1 + 0.2, 1e-300, 1.2345678901234567, 42.0 })
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
