using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class StringResolver_Tests
    {
        [Fact]
        public void PadRight_translates_to_rpad()
        {
            var expression = new BsonMapper().GetExpression<Row, string>(x => x.Value.PadRight(5, '0'));

            expression.Source.Should().Be("RPAD($.Value,@p0,@p1)");
            expression.Parameters["p0"].AsInt32.Should().Be(5);
            expression.Parameters["p1"].AsString.Should().Be("0");
        }

        private sealed class Row
        {
            public string Value { get; set; }
        }
    }
}
