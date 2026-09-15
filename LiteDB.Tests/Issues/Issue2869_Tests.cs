using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2869_Tests
    {
        [Theory]
        [InlineData(int.MinValue)]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(int.MaxValue)]
        public void Int32_values_implicitly_widen_to_Int64_and_Double(int expected)
        {
            var persisted = BsonSerializer.Deserialize(
                BsonSerializer.Serialize(new BsonDocument { ["number"] = expected }))["number"];

            persisted.Type.Should().Be(BsonType.Int32);

            long asLong = persisted;
            double asDouble = persisted;

            asLong.Should().Be(expected);
            asDouble.Should().Be(expected);
            persisted.Type.Should().Be(BsonType.Int32, "a conversion must not mutate its BSON source");
        }

        [Fact]
        public void Native_numeric_conversions_work_and_non_numeric_values_are_not_coerced()
        {
            BsonValue storedLong = long.MaxValue;
            BsonValue storedDouble = 1.25d;

            long asLong = storedLong;
            double asDouble = storedDouble;

            asLong.Should().Be(long.MaxValue);
            asDouble.Should().Be(1.25d);

            BsonValue text = "42";
            Action textToLong = () => ConsumeLong(text);
            Action textToDouble = () => ConsumeDouble(text);
            textToLong.Should().Throw<InvalidCastException>();
            textToDouble.Should().Throw<InvalidCastException>();
        }

        private static void ConsumeLong(long value) { }
        private static void ConsumeDouble(double value) { }
    }
}
