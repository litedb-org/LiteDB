using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class BooleanResult_Tests
    {
        [Theory]
        [InlineData("= @value")]
        [InlineData("!= @value")]
        [InlineData("> @value")]
        [InlineData(">= @value")]
        [InlineData("< @value")]
        [InlineData("<= @value")]
        [InlineData("LIKE @value")]
        [InlineData("IN @value")]
        [InlineData("BETWEEN @value AND @value")]
        public void Empty_quantifiers_return_boolean_results_without_evaluating_elements(string operation)
        {
            var parameters = new BsonDocument { ["items"] = new BsonArray(), ["value"] = "a" };
            foreach (var all in new[] { false, true })
            {
                var expression = BsonExpression.Create("@items " + (all ? "ALL " : "ANY ") + operation, parameters);
                var value = expression.ExecuteScalar();
                value.Type.Should().Be(BsonType.Boolean);
                value.AsBoolean.Should().Be(all);
            }
        }

        [Theory]
        [InlineData("AND", false)]
        [InlineData("AND", true)]
        [InlineData("OR", false)]
        [InlineData("OR", true)]
        public void Logical_results_preserve_short_circuiting_and_required_type_errors(string operation, bool guard)
        {
            var parameters = new BsonDocument { ["guard"] = guard, ["bad"] = 1 };
            var expression = BsonExpression.Create("(@guard = true) " + operation + " @bad", parameters);
            var evaluatesRight = operation == "AND" ? guard : !guard;
            if (evaluatesRight)
            {
                Action execute = () => expression.ExecuteScalar();
                execute.Should().Throw<InvalidCastException>();
            }
            else
            {
                expression.ExecuteScalar().AsBoolean.Should().Be(guard);
            }

            parameters["bad"] = false;
            expression.ExecuteScalar().AsBoolean.Should().Be(operation == "AND" ? false : guard);
        }

        [Theory]
        [InlineData("en-US/Ordinal", false)]
        [InlineData("en-US/IgnoreCase", true)]
        [InlineData("en-US/IgnoreCase, IgnoreNonSpace", true)]
        public void Bound_predicate_projections_keep_current_values_and_collation(string culture, bool equal)
        {
            var collation = new Collation(culture);
            var template = BsonExpression.Create("{ flags: [Name = @name, Name != @name, Name LIKE @name, " +
                "Name IN [@name], Name BETWEEN @name AND @name, Score > @minimum, Score >= @minimum, " +
                "Score < @minimum, Score <= @minimum, Score > @minimum AND Name = @name, " +
                "Score < @minimum OR Name = @name] }");
            var document = new BsonDocument { ["Name"] = "Alpha", ["Score"] = 5 };
            var low = template.Bind(new BsonDocument { ["name"] = "alpha", ["minimum"] = 4 });
            var high = template.Bind(new BsonDocument { ["name"] = "alpha", ["minimum"] = 6 });
            Check(low, new[] { equal, !equal, equal, equal, equal, true, true, false, false, equal, equal });
            Check(high, new[] { equal, !equal, equal, equal, equal, false, false, true, true, false, true });
            Check(low, new[] { equal, !equal, equal, equal, equal, true, true, false, false, equal, equal });

            void Check(BsonExpression expression, bool[] expected)
            {
                var result = expression.ExecuteScalar(document, collation).AsDocument;
                result["flags"].AsArray.Select(x => x.Type).Should().OnlyContain(x => x == BsonType.Boolean);
                result["flags"].AsArray.Select(x => x.AsBoolean).Should().Equal(expected);
                BsonSerializer.Deserialize(BsonSerializer.Serialize(result))["flags"].AsArray
                    .Select(x => x.AsBoolean).Should().Equal(expected);
                JsonSerializer.Deserialize(JsonSerializer.Serialize(result))["flags"].AsArray
                    .Select(x => x.AsBoolean).Should().Equal(expected);
            }
        }

        [Fact]
        public void Quantifiers_stop_before_an_unused_element_throws()
        {
            BsonExpressionOperators.LIKE_ANY(Collation.Binary, FirstThenThrow("abc"), "a%").AsBoolean.Should().BeTrue();
            BsonExpressionOperators.LIKE_ALL(Collation.Binary, FirstThenThrow(1), "a%").AsBoolean.Should().BeFalse();
            BsonExpressionOperators.IN_ANY(Collation.Binary, FirstThenThrow(1), new BsonArray(1, 2)).AsBoolean.Should().BeTrue();
            BsonExpressionOperators.IN_ALL(Collation.Binary, FirstThenThrow(3), new BsonArray(1, 2)).AsBoolean.Should().BeFalse();
            BsonExpressionOperators.BETWEEN_ANY(Collation.Binary, FirstThenThrow(1), new BsonArray(0, 2)).AsBoolean.Should().BeTrue();
            BsonExpressionOperators.BETWEEN_ALL(Collation.Binary, FirstThenThrow(3), new BsonArray(0, 2)).AsBoolean.Should().BeFalse();

            IEnumerable<BsonValue> FirstThenThrow(BsonValue value)
            {
                yield return value;
                throw new InvalidOperationException("Unused element was evaluated.");
            }
        }

        [Fact]
        public void Concurrent_nested_projections_share_no_mutable_result_containers()
        {
            var template = BsonExpression.Create("{ flags: ARRAY(MAP(Values => @ >= @minimum AND @ < @maximum)) }");
            var document = new BsonDocument { ["Values"] = new BsonArray(0, 1, 2, 3) };
            Parallel.For(0, 128, i =>
            {
                var minimum = i % 4;
                var bound = template.Bind(new BsonDocument { ["minimum"] = minimum, ["maximum"] = minimum + 1 });
                var first = bound.ExecuteScalar(document).AsDocument;
                var second = bound.ExecuteScalar(document).AsDocument;
                first["flags"].AsArray[minimum] = false;
                first["flags"] = new BsonArray();
                second["flags"].AsArray.Select(x => x.AsBoolean).Should().Equal(Enumerable.Range(0, 4).Select(x => x == minimum));
                bound.ExecuteScalar(document)["flags"].AsArray.Select(x => x.AsBoolean)
                    .Should().Equal(Enumerable.Range(0, 4).Select(x => x == minimum));
            });
        }
    }
}
