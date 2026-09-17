using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Tests.Mapper;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class ExpressionSourceAllocation_Tests
    {
        [Theory]
        [InlineData("Score + 1")]
        [InlineData("COUNT(*)")]
        [InlineData("{ n: COUNT(*), score: FIRST(*.Score) }")]
        [InlineData("IIF(Score > 1, COUNT(*), 0)")]
        [InlineData("MAP(Values => @ + $.Score)")]
        [InlineData("ARRAY(MAP(Values => COUNT(*)))")]
        [InlineData("Values[@ > $.Score]")]
        [InlineData("SORT(Values => @)")]
        public void Singleton_execution_matches_explicit_source_execution(string source)
        {
            BsonExpression expression;
            using (var scope = new DirectTranslationScope())
            {
                Tokenizer.ForbidCreation = false;
                expression = BsonExpression.Create(source);
            }
            var document = new BsonDocument { ["Score"] = 3, ["Values"] = new BsonArray(1, 5, 7) };
            var expected = expression.Execute(new[] { document }, document, document, Collation.Binary).ToArray();
            expression.Execute(document).ToArray().Should().Equal(expected);
            if (expression.IsScalar) expression.ExecuteScalar(document).Should().Be(expected.Single());
        }

        [Fact]
        public void Parameterized_source_references_still_receive_the_current_singleton()
        {
            var template = BsonExpression.Create("COUNT(*) + @offset", new BsonDocument { ["offset"] = 1 });
            template.ExecuteScalar(new BsonDocument()).AsInt32.Should().Be(2);
            template.Bind(new BsonDocument { ["offset"] = 5 }).ExecuteScalar(new BsonDocument()).AsInt32.Should().Be(6);
            template.ExecuteScalar().AsInt32.Should().Be(1); // Scalar no-root overload historically has empty input.
            template.Execute().Single().AsInt32.Should().Be(2); // Enumerable no-root overload has one empty document.
        }

        [Fact]
        public void Null_documents_still_fail_before_execution()
        {
            var expression = BsonExpression.Create("Score");
            Action scalar = () => expression.ExecuteScalar((BsonDocument)null);
            Action enumerable = () => expression.Execute((BsonDocument)null);
            scalar.Should().Throw<ArgumentNullException>();
            enumerable.Should().Throw<ArgumentNullException>();
        }
    }
}
