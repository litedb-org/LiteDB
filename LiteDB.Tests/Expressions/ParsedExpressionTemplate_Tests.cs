using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Tests.Mapper;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class ParsedExpressionTemplate_Tests
    {
        [Theory]
        [InlineData("Score >= @minimum AND (Name = @name OR Values ANY IN @keys)")]
        [InlineData("ARRAY(MAP(Values => @ + @delta))")]
        [InlineData("ARRAY(Values[@ > @minimum])")]
        [InlineData("Values[@index]")]
        [InlineData("ARRAY(SORT(Values => @ * @direction))")]
        [InlineData("SUM(*.Score) + @delta")]
        [InlineData("ARRAY(MAP([[1,2],[3,4]] => ARRAY(MAP(@ => @ + @delta))))")]
        [InlineData("@payload")]
        [InlineData("$")]
        [InlineData("$.items[01]")]
        [InlineData("Values ALL BETWEEN @minimum AND @maximum")]
        [InlineData("IIF(@guard, @delta, @minimum)")]
        public void Repeated_construction_matches_fresh_metadata_and_execution(string source)
        {
            var document = new BsonDocument { ["Score"] = 5, ["Name"] = "Alpha", ["Values"] = new BsonArray(1, 4, 2), ["items"] = new BsonArray(10, 20) };
            foreach (var i in new[] { 0, 1, 2, 0, 3 })
            {
                var parameters = new BsonDocument
                {
                    ["minimum"] = i, ["maximum"] = i + 3, ["name"] = "alpha", ["keys"] = new BsonArray(i, 5),
                    ["delta"] = i + 1, ["index"] = i % 3, ["direction"] = i % 2 == 0 ? 1 : -1,
                    ["guard"] = i % 2 == 0, ["payload"] = new BsonDocument { ["needle"] = i }
                };
                BsonExpression expected;
                using (var scope = new DirectTranslationScope())
                {
                    Tokenizer.ForbidCreation = false;
                    expected = BsonExpression.Create(source, parameters);
                }
                var actual = BsonExpression.Create(source, parameters);
                ExpressionParity.AssertMetadata(actual, expected);
                foreach (var collation in new[] { Collation.Binary, new Collation("en-US/IgnoreCase") })
                {
                    var left = actual.UseSource ? actual.Execute(new[] { document, document }, collation) : actual.Execute(document, collation);
                    var right = expected.UseSource ? expected.Execute(new[] { document, document }, collation) : expected.Execute(document, collation);
                    left.ToArray().Should().Equal(right.ToArray());
                }
                actual.Fields.Clear();
                actual.Left?.Fields.Clear();
                actual.Right?.Fields.Clear();
            }
        }

        [Fact]
        public void Resident_templates_avoid_tokenizers_but_the_fresh_test_scope_still_parses()
        {
            const string source = "ARRAY(MAP(@values => @ + @delta))";
            BsonExpression.Create(source);
            BsonExpression.Create(source);
            using var scope = new DirectTranslationScope();
            BsonExpression.DisableCompilationCache = false;
            BsonExpression.Create(source, new BsonDocument { ["values"] = new BsonArray(1), ["delta"] = 5 })
                .ExecuteScalar().AsArray.Select(x => x.AsInt32).Should().Equal(6);
            BsonExpression.DisableCompilationCache = true;
            Action fresh = () => BsonExpression.Create(source);
            fresh.Should().Throw<InvalidOperationException>();
        }

        [Theory]
        [InlineData("MAP([1] => @missing)")]
        [InlineData("Values[@missing]")]
        [InlineData("ARRAY(Values[@ > @missing])")]
        public void Explicit_null_parameter_documents_keep_their_execution_errors(string source)
        {
            for (var i = 0; i < 4; i++)
            {
                var expression = BsonExpression.Create(source, (BsonDocument)null);
                Assert.Null(expression.Parameters);
                Action execute = () => expression.Execute(new BsonDocument { ["Values"] = new BsonArray(1) }).ToArray();
                execute.Should().Throw<NullReferenceException>();
            }
        }

        [Fact]
        public void Volatile_values_and_mutable_results_are_evaluated_for_every_call()
        {
            const string source = "{ id: GUID(), nested: { values: [1,2] }, caller: @payload }";
            BsonExpression.Create(source);
            BsonExpression.Create(source);
            var payload = new BsonDocument { ["value"] = 1 };
            var parameters = new BsonDocument { ["payload"] = payload };
            var first = BsonExpression.Create(source, parameters).ExecuteScalar();
            first["nested"]["values"].AsArray.Add(99);
            var second = BsonExpression.Create(source, parameters).ExecuteScalar();
            second["id"].Should().NotBe(first["id"]);
            second["nested"]["values"].AsArray.Select(x => x.AsInt32).Should().Equal(1, 2);
            second["caller"].Should().BeSameAs(payload);
            BsonExpression.Create("'UPPER'").ExecuteScalar().AsString.Should().Be("UPPER");
            BsonExpression.Create("'upper'").ExecuteScalar().AsString.Should().Be("upper");
        }

        [Fact]
        public void Execution_errors_do_not_replace_templates_or_capture_bad_values()
        {
            const string source = "SUBSTRING(@name, @start)";
            var parameters = new BsonDocument { ["name"] = "Alpha", ["start"] = 0 };
            BsonExpression.Create(source, parameters);
            BsonExpression.Create(source, parameters);
            var first = BsonExpression.Create(source, parameters);
            parameters["start"] = 99;
            Action execute = () => first.ExecuteScalar();
            execute.Should().Throw<ArgumentOutOfRangeException>();
            parameters["start"] = 1;
            first.ExecuteScalar().AsString.Should().Be("lpha");
            BsonExpression.Create(source, new BsonDocument { ["name"] = "Beta", ["start"] = 2 })
                .ExecuteScalar().AsString.Should().Be("ta");
        }

        [Fact]
        public void Oversized_input_and_invalid_trailing_tokens_keep_parser_behavior()
        {
            var source = "@value" + new string(' ', ParsedExpressionCache.MaximumExpressionLength);
            BsonExpression.Create(source);
            BsonExpression.Create(source);
            using (var scope = new DirectTranslationScope())
            {
                BsonExpression.DisableCompilationCache = false;
                Action oversized = () => BsonExpression.Create(source);
                oversized.Should().Throw<InvalidOperationException>();
            }
            for (var i = 0; i < 3; i++)
            {
                foreach (var invalidSource in new[] { "1; 2", "@payload.needle", "@values[@index]" })
                {
                    Action invalid = () => BsonExpression.Create(invalidSource);
                    invalid.Should().Throw<LiteException>();
                }
            }
            var tokenizer = new Tokenizer("1 + 2, 4");
            BsonExpression.Create("1 + 2");
            BsonExpression.Create("1 + 2");
            BsonExpression.Create(tokenizer, BsonExpressionParserMode.Full, new BsonDocument()).ExecuteScalar().AsInt32.Should().Be(3);
            tokenizer.ReadToken().Expect(TokenType.Comma);
            BsonExpression.Create(tokenizer, BsonExpressionParserMode.Full, new BsonDocument()).ExecuteScalar().AsInt32.Should().Be(4);
            tokenizer.LookAhead().Expect(TokenType.EOF);
        }
    }
}
