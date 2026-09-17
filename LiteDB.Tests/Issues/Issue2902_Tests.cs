using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2902_Tests
    {
        [Fact]
        [Trait("Category", "PendingBug")]
        public void Mixed_numeric_equality_is_transitive_for_adjacent_doubles()
        {
            var first = new BsonValue(0.1d);
            var adjacent = new BsonValue(BitConverter.Int64BitsToDouble(
                BitConverter.DoubleToInt64Bits(0.1d) + 1));
            var decimalValue = new BsonValue(0.1m);

            var bothEqualTheDecimal = first.Equals(decimalValue) &&
                decimalValue.Equals(adjacent);

            bothEqualTheDecimal.Should().BeFalse(
                "two unequal doubles cannot both equal the same decimal value");
            first.Equals(adjacent).Should().BeFalse();
        }

        [Fact]
        [Trait("Category", "PendingBug")]
        public void Smallest_positive_double_is_not_equal_to_decimal_zero()
        {
            new BsonValue(double.Epsilon).Equals(new BsonValue(0m)).Should().BeFalse();
        }
    }
}
