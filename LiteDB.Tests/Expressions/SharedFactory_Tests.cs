using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class SharedFactory_Tests
    {
        [Theory]
        [InlineData("9223372036854775807")]
        [InlineData("-9223372036854775808")]
        [InlineData("2147483648")]
        public void Integer_literals_preserve_expression_syntax_instead_of_extended_json(string source)
        {
            var expression = BsonExpression.Create(source);
            expression.Source.Should().Be(source);
            expression.Type.Should().Be(BsonExpressionType.Int);
            expression.ExecuteScalar().AsInt64.Should().Be(long.Parse(source));
        }

        [Theory]
        [InlineData("$.items[01]")]
        [InlineData("$.items[-0]")]
        [InlineData("$.items[-01]")]
        public void Persisted_index_path_spelling_is_preserved(string source)
        {
            BsonExpression.Create(source).Source.Should().Be(source);
        }

        [Fact]
        public void Parsed_nested_functions_and_path_filters_accept_independent_bindings()
        {
            var document = new BsonDocument { ["items"] = new BsonArray { 1, 5, 9 } };
            foreach (var source in new[] { "ARRAY(FILTER($.items => @ > @minimum))", "ARRAY($.items[@ > @minimum])" })
            {
                var template = BsonExpression.Create(source, new BsonDocument { ["minimum"] = 0 });
                var low = template.Bind(new BsonDocument { ["minimum"] = 2 });
                var high = template.Bind(new BsonDocument { ["minimum"] = 7 });
                low.ExecuteScalar(document).AsArray.Select(x => x.AsInt32).Should().Equal(5, 9);
                high.ExecuteScalar(document).AsArray.Select(x => x.AsInt32).Should().Equal(9);
                low.ExecuteScalar(document).AsArray.Select(x => x.AsInt32).Should().Equal(5, 9);
            }
        }
    }
}
