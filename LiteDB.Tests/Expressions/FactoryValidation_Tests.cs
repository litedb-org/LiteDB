using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class FactoryValidation_Tests
    {
        [Theory]
        [InlineData("1 + $.items[*]")]
        [InlineData("1 = $.items[*]")]
        [InlineData("$.other[*] ANY = $.items[*]")]
        [InlineData("$.other[*] ALL = $.items[*]")]
        public void Enumerable_right_operand_reports_the_right_expression(string source)
        {
            Action parse = () => BsonExpression.Create(source);
            parse.Should().Throw<LiteException>().WithMessage("Right expression `$.items[*]` must return a single value");
        }

        [Fact]
        public void Enumerable_left_operand_still_requires_a_quantifier()
        {
            Action parse = () => BsonExpression.Create("$.items[*] = 1");
            parse.Should().Throw<LiteException>().WithMessage("Left expression `$.items[*]` returns more than one result*");
            BsonExpression.Create("$.items[*] ANY = 1").ExecuteScalar(new BsonDocument
                { ["items"] = new BsonArray(1, 2) }).AsBoolean.Should().BeTrue();
        }

        [Theory]
        [InlineData("IIF(RANDOM() > 0, 1, 0)", false, true)]
        [InlineData("IIF(true, RANDOM(), 0)", false, true)]
        [InlineData("IIF(false, 1, RANDOM())", false, true)]
        [InlineData("IIF(@flag, 1, 0)", false, false)]
        [InlineData("IIF(true, @value, 0)", false, false)]
        [InlineData("IIF(false, 1, @value)", false, false)]
        [InlineData("IIF(true, 1, 0)", true, false)]
        public void Conditional_metadata_requires_every_operand_to_be_immutable(string source, bool immutable, bool volatileValue)
        {
            var expression = BsonExpression.Create(source);
            expression.IsImmutable.Should().Be(immutable);
            expression.IsVolatile.Should().Be(volatileValue);
            expression.Bind(new BsonDocument { ["flag"] = true, ["value"] = 3 }).IsImmutable.Should().Be(immutable);
        }
    }
}
