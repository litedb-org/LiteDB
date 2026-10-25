using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Tests.Mapper;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class ParsedExpressionCache_Tests
    {
        [Fact]
        public void Admission_copies_recurring_templates_before_callers_can_mutate_them()
        {
            var cache = new ParsedExpressionCache();
            const string source = "Score > @minimum";
            var expression = BsonExpression.Create(source, new BsonDocument { ["minimum"] = 1 });
            cache.Add(source, expression, repeated: false);
            cache.TryGet(source, out var absent, out var repeated).Should().BeFalse();
            absent.Should().BeNull();
            repeated.Should().BeTrue();
            cache.Add(source, expression, repeated);
            expression.Fields.Clear();
            expression.Left.Fields.Clear();
            expression.Parameters["minimum"] = 999;
            cache.Add(source, expression, repeated: false); // A late first-use completion cannot demote it.
            cache.TryGet(source, out var template, out _).Should().BeTrue();
            Assert.Null(template.Parameters);
            Assert.Null(template.Right.Parameters);
            template.Fields.Should().Equal("Score");
            template.Left.Fields.Should().Equal("Score");
            template.Bind(new BsonDocument { ["minimum"] = 2 }).ExecuteScalar(new BsonDocument { ["Score"] = 3 })
                .AsBoolean.Should().BeTrue();
        }

        [Fact]
        public void Capacity_and_text_length_are_bounded_and_recent_entries_survive()
        {
            var cache = new ParsedExpressionCache(2);
            var expression = BsonExpression.Create("@value");
            cache.Add("@value + 1", expression, true);
            cache.Add("@value + 2", expression, true);
            cache.TryGet("@value + 1", out _, out _).Should().BeTrue();
            cache.Add("@value + 3", expression, true);
            cache.Count.Should().Be(2);
            cache.TryGet("@value + 1", out _, out _).Should().BeTrue();
            cache.TryGet("@value + 2", out _, out _).Should().BeFalse();
            var longSource = "@value" + new string(' ', ParsedExpressionCache.MaximumExpressionLength);
            cache.Add(longSource, expression, true);
            cache.TryGet(longSource, out _, out _).Should().BeFalse();
            cache.Count.Should().Be(2);
        }

        [Fact]
        public void Concurrent_publication_and_eviction_keep_bindings_independent()
        {
            var cache = new ParsedExpressionCache(4);
            Parallel.For(0, 16, worker =>
            {
                var source = "@value + " + worker;
                var expression = BsonExpression.Create(source, new BsonDocument { ["value"] = worker });
                for (var i = 0; i < 100; i++)
                {
                    cache.Add(source, expression, true);
                    if (cache.TryGet(source, out var template, out _))
                    {
                        template.Bind(new BsonDocument { ["value"] = i }).ExecuteScalar().AsInt32.Should().Be(i + worker);
                    }
                    cache.Count.Should().BeInRange(1, 4);
                }
            });
        }

        [Fact]
        public void Public_template_cache_does_not_retain_parameter_payloads_or_original_nodes()
        {
            var source = "{ marker: '" + Guid.NewGuid().ToString("N") + "', values: ARRAY(MAP(@values => @ + @delta)), picked: Values[@index] }";
            var references = Populate(source);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            references.Should().OnlyContain(x => !x.IsAlive);
            using var scope = new DirectTranslationScope();
            BsonExpression.DisableCompilationCache = false;
            var result = BsonExpression.Create(source, new BsonDocument
            {
                ["values"] = new BsonArray(1, 2), ["delta"] = 3, ["index"] = 1
            }).ExecuteScalar(new BsonDocument { ["Values"] = new BsonArray(1, 2) });
            result["values"].AsArray.Select(x => x.AsInt32).Should().Equal(4, 5);
            result["picked"].AsInt32.Should().Be(2);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] Populate(string source)
        {
            var values = new BsonArray(Enumerable.Range(0, 1000).Select(i => new BsonValue(i)));
            var parameters = new BsonDocument { ["values"] = values, ["delta"] = 1, ["index"] = 0, ["payload"] = new byte[1024 * 1024] };
            var first = BsonExpression.Create(source, parameters);
            var second = BsonExpression.Create(source, parameters);
            return new[] { new WeakReference(parameters), new WeakReference(values), new WeakReference(parameters["payload"].AsBinary),
                new WeakReference(first), new WeakReference(second) };
        }
    }
}
