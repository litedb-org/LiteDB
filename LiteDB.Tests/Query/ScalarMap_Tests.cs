using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Tests.Mapper;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class ScalarMap_Tests
    {
        [Theory]
        [InlineData("MAP(Values => @ + @delta)", "[4,6]")]
        [InlineData("MAP(Values => null)", "[null,null]")]
        [InlineData("MAP(Values => [@,@ + @delta])", "[[1,4],[3,6]]")]
        [InlineData("MAP(Values => { value: @, offset: @delta })", "[{value:1,offset:3},{value:3,offset:3}]")]
        [InlineData("MAP(Values => ITEMS([@,@ + @delta]))", "[1,4,3,6]")]
        [InlineData("MAP(Values => COUNT(*) + @)", "[2,4]")]
        [InlineData("MAP(Values => MAP([@,@ + 1] => @ + @delta))", "[4,5,6,7]")]
        public void Scalar_and_enumerable_selectors_preserve_values_and_flattening(string source, string expected)
        {
            BsonExpression expression;
            using (var scope = new DirectTranslationScope())
            {
                Tokenizer.ForbidCreation = false;
                expression = BsonExpression.Create(source, new BsonDocument { ["delta"] = 3 });
            }
            expression.Execute(new BsonDocument { ["Values"] = new BsonArray(1, 3) })
                .Should().Equal(JsonSerializer.Deserialize(expected).AsArray.ToArray());
        }

        [Fact]
        public void Scalar_mapping_remains_lazy_and_disposes_its_input_after_early_exit()
        {
            var count = 0;
            var disposed = false;
            IEnumerable<BsonValue> Input()
            {
                try
                {
                    count++; yield return 1;
                    count++; yield return 2;
                }
                finally { disposed = true; }
            }
            var mapper = BsonExpression.Create("@ + @delta");
            var result = BsonExpressionFunctions.MAP(new BsonDocument(), Collation.Binary,
                new BsonDocument { ["delta"] = 5 }, Input(), mapper);
            count.Should().Be(0);
            result.First().AsInt32.Should().Be(6);
            count.Should().Be(1);
            disposed.Should().BeTrue();
        }

        [Fact]
        public void Scalar_selector_errors_remain_deferred_until_enumeration()
        {
            var expression = BsonExpression.Create("MAP(Values => $.Values[@index])", new BsonDocument { ["index"] = "bad" });
            var result = expression.Execute(new BsonDocument { ["Values"] = new BsonArray(1, 3) });
            using var iterator = result.GetEnumerator();
            Action read = () => iterator.MoveNext();
            read.Should().Throw<LiteException>().WithMessage("*must return number*");
        }
    }
}
