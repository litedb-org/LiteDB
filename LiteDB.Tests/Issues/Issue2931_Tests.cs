using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2931_Tests
    {
        [Fact]
        public void Collation_Compare_BsonValue_parameters_are_spelled_left_and_right()
        {
            var compare = typeof(Collation).GetMethod(nameof(Collation.Compare), new[] { typeof(BsonValue), typeof(BsonValue) });

            compare.GetParameters().Select(x => x.Name).Should().Equal("left", "right");
        }

        [Fact]
        public void Named_arguments_compile_and_compare()
        {
            var collation = new Collation("en-US/IgnoreCase");

            collation.Compare(left: new BsonValue("a"), right: new BsonValue("B")).Should().BeNegative();
            collation.Compare(left: new BsonValue(2), right: new BsonValue(1)).Should().BePositive();
        }
    }
}
