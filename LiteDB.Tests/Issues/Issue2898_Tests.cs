using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2898_Tests
    {
        [Theory]
        [InlineData(0UL)]
        [InlineData(1UL)]
        [InlineData(9007199254740993UL)]
        [InlineData(9223372036854775807UL)]
        [InlineData(9223372036854775808UL)]
        [InlineData(ulong.MaxValue)]
        public void Boxed_UInt64_preserves_the_unsigned_bits(ulong value)
        {
            var boxedValue = new BsonValue((object)value);

            boxedValue.Type.Should().Be(BsonType.Int64);
            boxedValue.RawValue.Should().Be(unchecked((long)value));
            ((ulong)boxedValue).Should().Be(value);
            ((double)boxedValue).Should().Be((double)unchecked((long)value));
        }

        [Theory]
        [InlineData(0U)]
        [InlineData(1U)]
        [InlineData(uint.MaxValue)]
        public void Boxed_UInt32_matches_the_widened_BsonValue_conversion(uint value)
        {
            BsonValue widenedValue = (long)value;
            var boxedValue = new BsonValue((object)value);

            boxedValue.Type.Should().Be(BsonType.Int64);
            boxedValue.Should().Be(widenedValue);
            boxedValue.RawValue.Should().Be(widenedValue.RawValue);
        }
    }
}
