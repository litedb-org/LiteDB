using System;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using FluentAssertions;
using LiteDB.Tests.Mapper;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    public class ExpressionParameterLifetime_Tests
    {
        [Theory]
        [InlineData("ARRAY(MAP([1,2] => @ + @delta))")]
        [InlineData("ARRAY(FILTER([1,2,3] => @ > @delta))")]
        [InlineData("ARRAY(SORT([2,1] => @ + @delta))")]
        [InlineData("Values[@index]")]
        [InlineData("ARRAY(Values[@ > @delta])")]
        [InlineData("ARRAY(MAP([[1,2],[3,4]] => ARRAY(MAP(@ => @ + @delta))))")]
        public void Rebinding_nested_expressions_releases_the_original_parameter_document(string source)
        {
            var probe = Create(source);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            probe.Parameters.IsAlive.Should().BeFalse();
            probe.Payload.IsAlive.Should().BeFalse();
            var document = new BsonDocument { ["Values"] = new BsonArray(1, 2, 3) };
            probe.Bound.ExecuteScalar(document).Should().Be(probe.Expected);
            GC.KeepAlive(probe.Bound);
        }

        [Fact]
        public void Automatic_linq_templates_release_the_first_serialized_array()
        {
            var mapper = new BsonMapper();
            var original = TranslateLargeArray(mapper);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            original.IsAlive.Should().BeFalse();
            var result = mapper.GetExpression(Projection(new[] { 2 })).ExecuteScalar(
                new BsonDocument { ["Values"] = new BsonArray(1, 2, 3) });
            result.AsArray.Select(x => x.AsInt32).Should().Equal(2);
            GC.KeepAlive(mapper);
        }

        [Theory]
        [InlineData("MAP([1] => @missing)")]
        [InlineData("FILTER([1] => @missing)")]
        [InlineData("SORT([1] => @missing)")]
        [InlineData("Values[@missing]")]
        [InlineData("ARRAY(Values[@ > @missing])")]
        public void Explicitly_null_parameter_documents_keep_their_error_behavior(string source)
        {
            using var scope = new DirectTranslationScope();
            Tokenizer.ForbidCreation = false;
            var expression = BsonExpression.Create(source, (BsonDocument)null);
            Action execute = () => expression.Execute(new BsonDocument { ["Values"] = new BsonArray(1) }).ToArray();
            execute.Should().Throw<NullReferenceException>();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference TranslateLargeArray(BsonMapper mapper)
        {
            var expression = mapper.GetExpression(Projection(Enumerable.Range(1, 10000).ToArray()));
            return new WeakReference(expression.Parameters["p0"]);
        }

        private static Expression<Func<Row, int[]>> Projection(int[] keys) => x => x.Values.Where(v => keys.Contains(v)).ToArray();
        public class Row { public int[] Values { get; set; } }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Probe Create(string source)
        {
            using var scope = new DirectTranslationScope();
            Tokenizer.ForbidCreation = false;
            var parameters = new BsonDocument { ["delta"] = 7, ["index"] = 1, ["payload"] = new byte[1024 * 1024] };
            var binding = new BsonDocument { ["delta"] = 2, ["index"] = 0 };
            var expression = BsonExpression.Create(source, parameters).Bind(binding);
            var expected = BsonExpression.Create(source, binding).ExecuteScalar(new BsonDocument { ["Values"] = new BsonArray(1, 2, 3) });
            return new Probe
            {
                Bound = expression, Expected = expected,
                Parameters = new WeakReference(parameters), Payload = new WeakReference(parameters["payload"].AsBinary)
            };
        }

        private class Probe
        {
            internal BsonExpression Bound;
            internal BsonValue Expected;
            internal WeakReference Parameters;
            internal WeakReference Payload;
        }
    }
}
