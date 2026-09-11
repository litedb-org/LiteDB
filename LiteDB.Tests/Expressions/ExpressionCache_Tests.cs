using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class ExpressionCache_Tests
    {
        [Fact]
        public async Task ExpressionCache_CapHoldsUnderConcurrency()
        {
            var tasks = Enumerable.Range(0, 16)
                .Select(worker => Task.Run(() =>
                {
                    for (var i = 0; i < 200; i++)
                    {
                        var value = worker * 10000 + i;
                        if ((i & 1) == 0)
                        {
                            BsonExpression.Create("$.value = " + value);
                        }
                        else
                        {
                            BsonExpression.Create("$.items[*].field" + value);
                        }
                    }
                }))
                .ToArray();

            await Task.WhenAll(tasks);

            BsonExpression.CompiledExpressionCount.Should().BeInRange(1, 1000);
        }

        [Fact]
        public void QueryEq_LiteralValues_DoNotGrowExpressionCacheWithoutBound()
        {
            for (var i = 0; i < 3000; i++)
            {
                Query.EQ("value", i);
            }

            BsonExpression.CompiledExpressionCount.Should().BeInRange(1, 1000);
        }

        [Fact]
        public void CacheRollover_DoesNotRecompileNestedExpressionWithOuterContext()
        {
            var nested = BsonExpression.ParseAndCompile(
                new Tokenizer("$.nested"),
                BsonExpressionParserMode.Full,
                new BsonDocument(),
                DocumentScope.Root);

            // Guarantee that the nested expression's delegate is removed from
            // the process-wide cache while its instance remains compiled.
            for (var i = 0; i < 2000; i++)
            {
                BsonExpression.Create("$.rollover" + i);
            }

            var context = new ExpressionContext();
            var parent = new BsonExpression
            {
                Source = "expression-cache-rollover-parent",
                Type = BsonExpressionType.Path,
                IsImmutable = true,
                Parameters = new BsonDocument(),
                Left = nested,
                UseSource = false,
                Expression = context.Root,
                Fields = new HashSet<string> { "$" },
                IsScalar = true
            };

            BsonExpression.Compile(parent, context);

            var document = new BsonDocument { ["nested"] = 1 };
            parent.ExecuteScalar(document).Should().BeSameAs(document);
            BsonExpression.CompiledExpressionCount.Should().BeInRange(1, 1000);
        }
    }
}
