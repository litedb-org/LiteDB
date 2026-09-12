using System.Collections.Generic;
using System.Linq;
using System;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class ExpressionCache_Tests
    {
        [Fact]
        public void CollidingHotExpressions_CoexistInsteadOfRecompilingEachOther()
        {
            // Find keys that collide in the former single-entry cache under
            // this process's randomized string hash seed.
            var sources = Enumerable.Range(0, 100).Select(i => "hot-" + i)
                .GroupBy(source => (uint)StringComparer.Ordinal.GetHashCode(source) % 4)
                .First(group => group.Count() >= 4).Take(4).ToArray();
            var cache = new CompiledExpressionCache(4);
            foreach (var source in sources)
            {
                Func<string> compiled = () => source;
                cache.Add(source, compiled);
            }
            foreach (var source in sources) cache.Get<Func<string>>(source)().Should().Be(source);
            cache.Count.Should().Be(4);
        }

        [Fact]
        public void ScalarAndEnumerableDelegates_WithTheSameSource_Coexist()
        {
            var cache = new CompiledExpressionCache(4);
            cache.Add("$", (Func<int>)(() => 42));
            cache.Add("$", (Func<IEnumerable<int>>)(() => new[] { 42 }));
            cache.Get<Func<int>>("$")().Should().Be(42);
            cache.Get<Func<IEnumerable<int>>>("$")().Should().Equal(42);
            cache.Count.Should().Be(2);
        }

        [Fact]
        public async Task Colliding_Entries_Are_Atomic_And_Stay_Bounded()
        {
            var cache = new CompiledExpressionCache(1);
            var tasks = Enumerable.Range(0, 16).Select(worker => Task.Run(() =>
            {
                var source = "expression-" + worker;
                System.Func<int> compiled = () => worker;
                for (var i = 0; i < 1000; i++)
                {
                    cache.Add(source, compiled);
                    var found = cache.Get<System.Func<int>>(source);
                    if (found != null) found().Should().Be(worker);
                    cache.Count.Should().BeInRange(0, 1);
                }
            }));

            await Task.WhenAll(tasks);
            cache.Count.Should().Be(1);
        }

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
