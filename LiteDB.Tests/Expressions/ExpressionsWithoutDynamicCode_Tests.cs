// .NET Framework always has dynamic code, and its System.Linq.Expressions has neither the interpreter switch nor the counter.
#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Expressions
{
    /// <summary>
    /// Runtimes without dynamic code (Mono full AOT on iOS, #2804) run expression trees through the
    /// System.Linq.Expressions interpreter. The interpreter has prebuilt thunks for delegates with at most two
    /// parameters; for anything larger it emits a DynamicMethod, which is exactly what such a runtime cannot do.
    /// </summary>
    [CollectionDefinition(nameof(ExpressionsWithoutDynamicCode_Tests), DisableParallelization = true)]
    public class ExpressionsWithoutDynamicCodeCollection
    {
    }

    // Flips a process-wide switch, so it may not run while other tests compile expressions.
    [Collection(nameof(ExpressionsWithoutDynamicCode_Tests))]
    public class ExpressionsWithoutDynamicCode_Tests
    {
        private delegate BsonValue FiveParameters(IEnumerable<BsonDocument> source, BsonDocument root, BsonValue current, Collation collation, BsonDocument parameters);

        /// <summary>
        /// The number of delegates the interpreter had to build with Reflection.Emit. Null when this runtime's
        /// System.Linq.Expressions does not have the counter (.NET Framework).
        /// </summary>
        private static int? EmittedThunks()
        {
            var field = typeof(Expression).Assembly
                .GetType("System.Dynamic.Utils.DelegateHelpers")
                ?.GetField("s_ThunksCreated", BindingFlags.NonPublic | BindingFlags.Static);

            return field == null ? (int?)null : (int)field.GetValue(null);
        }

        [Fact]
        public void Interpreting_A_Five_Parameter_Delegate_Needs_Reflection_Emit()
        {
            // Control: proves the counter detects what it is meant to detect on this runtime.
            var before = EmittedThunks();
            if (before == null) return;

            var parameters = new[]
            {
                Expression.Parameter(typeof(IEnumerable<BsonDocument>)), Expression.Parameter(typeof(BsonDocument)),
                Expression.Parameter(typeof(BsonValue)), Expression.Parameter(typeof(Collation)), Expression.Parameter(typeof(BsonDocument))
            };

            Expression.Lambda<FiveParameters>(parameters[2], parameters).Compile(preferInterpretation: true);

            EmittedThunks().Should().BeGreaterThan(before.Value);
        }

        private static int Double(int value) => value * 2;

        [Fact]
        public void Linq_Values_Are_Evaluated_Without_Reflection_Emit()
        {
            // A method call the LINQ translator cannot express is evaluated through an interpreted Func<object>,
            // which has no parameters and therefore a prebuilt thunk.
            var before = EmittedThunks();
            var seed = 21;

            var expression = new BsonMapper().GetExpression<BsonDocument, bool>(x => x["a"] == Double(seed));

            expression.Execute(new BsonDocument { ["a"] = 42 }).Single().AsBoolean.Should().BeTrue();
            EmittedThunks().Should().Be(before);
        }

        [Fact]
        public void Expressions_Run_Without_Reflection_Emit_When_Dynamic_Code_Is_Unavailable()
        {
            var document = new BsonDocument
            {
                ["name"] = "Ada",
                ["age"] = 36,
                ["items"] = new BsonArray { 3, 1, 2 },
                ["nested"] = new BsonDocument { ["x"] = 5 }
            };

            // Unique sources: a compiled expression is cached by its source text.
            var tag = Guid.NewGuid().ToString("N");
            var expressions = new Dictionary<string, string>
            {
                [$"UPPER($.name) + '{tag}'"] = "\"ADA" + tag + "\"",
                [$"$.age + @0 + LENGTH('{tag}')"] = "72",
                [$"ARRAY(MAP($.items[*] => @ * $.nested.x + LENGTH('{tag}')))"] = "[47,37,42]",
                [$"ARRAY(FILTER($.items[*] => @ > 1 AND '{tag}' != ''))"] = "[3,2]",
                [$"{{ n: $.name, big: $.age > 30, tag: '{tag}' }}"] = "{\"n\":\"Ada\",\"big\":true,\"tag\":\"" + tag + "\"}",
                [$"$.items[*] ANY = 2 AND '{tag}' != ''"] = "true"
            };

            var original = BsonExpressionCompiler.UseSingleArgumentDelegates;
            BsonExpressionCompiler.UseSingleArgumentDelegates = true;

            try
            {
                var before = EmittedThunks();

                foreach (var pair in expressions)
                {
                    var results = BsonExpression.Create(pair.Key, new BsonValue(4)).Execute(document).ToArray();

                    JsonSerializer.Serialize(results.Single()).Should().Be(pair.Value, "`{0}`", pair.Key);
                }

                EmittedThunks().Should().Be(before, "no expression may require a delegate that the interpreter can only build with Reflection.Emit");
            }
            finally
            {
                BsonExpressionCompiler.UseSingleArgumentDelegates = original;
            }
        }
    }
}
#endif
