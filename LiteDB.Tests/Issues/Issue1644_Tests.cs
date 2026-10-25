using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1644_Tests
    {
        [Fact]
        public async Task Concurrent_first_execution_of_cached_boolean_shapes_keeps_each_parameter_set()
        {
            var work = Enumerable.Range(0, 16).Select(index => Task.Run(() =>
            {
                var expression = BsonExpression.Create("(value = @0 OR value = @1) AND enabled = true", index + 1, index + 101);
                for (var round = 0; round < 30; round++)
                {
                    var expected = index + 1;
                    foreach (var value in new[] { expected, expected + 100, expected + 1000 })
                        expression.ExecuteScalar(new BsonDocument { ["value"] = value, ["enabled"] = true })
                            .AsBoolean.Should().Be(value != expected + 1000);
                }
            }));
            await Task.WhenAll(work);
        }

        [Fact]
        public void Deferred_boolean_compilation_preserves_short_circuit_evaluation()
        {
            var guard = BsonExpression.Create("SUBSTRING('x', 999) = 'x'");
            Action evaluateGuard = () => guard.ExecuteScalar();
            evaluateGuard.Should().Throw<ArgumentOutOfRangeException>();
            Query.And(BsonExpression.Create("false"), guard)
                .ExecuteScalar().AsBoolean.Should().BeFalse();
            Query.Or(BsonExpression.Create("true"), guard)
                .ExecuteScalar().AsBoolean.Should().BeTrue();
        }
        [Fact]
        public async Task Concurrent_cache_publication_returns_the_same_winner()
        {
            var cache = new CompiledExpressionCache(4);
            var winners = await Task.WhenAll(Enumerable.Range(0, 32).Select(value => Task.Run(() =>
                cache.Add("shared", (Func<int>)(() => value)))));
            foreach (var winner in winners) winner.Should().BeSameAs(winners[0]);
            cache.Count.Should().Be(1);
        }

        [Fact]
        public void Discarded_unexecuted_boolean_trees_are_not_rooted_by_the_cache()
        {
            var tree = CreateDiscardedBooleanTree();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            tree.IsAlive.Should().BeFalse();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateDiscardedBooleanTree()
        {
            var expression = BsonExpression.Create("discard_" + Guid.NewGuid().ToString("N") + " = 1 OR true");
            return new WeakReference(expression.Expression);
        }

        [Fact]
        public void Deferred_boolean_compilation_publishes_only_after_execution()
        {
            var field = typeof(BsonExpression).GetField("_compiledCache", BindingFlags.Static | BindingFlags.NonPublic);
            var cache = (CompiledExpressionCache)field.GetValue(null);
            var expression = BsonExpression.Create("deferred_" + Guid.NewGuid().ToString("N") + " = 1 OR true");

            cache.Get<BsonExpressionScalarDelegate>(expression.Source).Should().BeNull();
            expression.ExecuteScalar().AsBoolean.Should().BeTrue();
            cache.Get<BsonExpressionScalarDelegate>(expression.Source).Should().NotBeNull();
        }
    }
}
