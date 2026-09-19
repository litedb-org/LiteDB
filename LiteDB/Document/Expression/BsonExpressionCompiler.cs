using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB
{
    /// <summary>
    /// Turns a parsed expression tree into the delegate a BsonExpression executes.
    /// </summary>
    internal static class BsonExpressionCompiler
    {
        /// <summary>
        /// A runtime without dynamic code runs expression trees through the System.Linq.Expressions interpreter.
        /// The interpreter has prebuilt thunks only for delegates with at most two parameters and builds any
        /// other delegate with Reflection.Emit. Native AOT substitutes its own mechanism for that step; Mono
        /// full AOT (iOS) does not, so a five-parameter expression delegate fails there with "Attempting to JIT
        /// compile method" (#2804). When this is set, expressions are compiled to a one-parameter delegate and
        /// adapted to the five-parameter one in ordinary code.
        /// </summary>
        internal static bool UseSingleArgumentDelegates { get; set; } = IsDynamicCodeSupported() == false;

        public static BsonExpressionScalarDelegate CompileScalar(Expression body, ExpressionContext context)
        {
            if (UseSingleArgumentDelegates == false)
            {
                return Expression.Lambda<BsonExpressionScalarDelegate>(body, context.Source, context.Root, context.Current, context.Collation, context.Parameters).Compile();
            }

            var run = CompileSingleArgument<BsonValue>(body, context);

            return (source, root, current, collation, parameters) => run(new Arguments(source, root, current, collation, parameters));
        }

        public static BsonExpressionEnumerableDelegate CompileEnumerable(Expression body, ExpressionContext context)
        {
            if (UseSingleArgumentDelegates == false)
            {
                return Expression.Lambda<BsonExpressionEnumerableDelegate>(body, context.Source, context.Root, context.Current, context.Collation, context.Parameters).Compile();
            }

            var run = CompileSingleArgument<IEnumerable<BsonValue>>(body, context);

            return (source, root, current, collation, parameters) => run(new Arguments(source, root, current, collation, parameters));
        }

        private static Func<Arguments, TResult> CompileSingleArgument<TResult>(Expression body, ExpressionContext context)
        {
            var arguments = Expression.Parameter(typeof(Arguments), "arguments");
            var rewritten = new ArgumentRewriter(context, arguments).Visit(body);

            // Func<Arguments, TResult> has one parameter and only reference types, so the interpreter binds it
            // to a prebuilt generic thunk whose shared instantiation exists on every AOT runtime.
            return Expression.Lambda<Func<Arguments, TResult>>(rewritten, arguments).Compile(preferInterpretation: true);
        }

        private static bool IsDynamicCodeSupported()
        {
#if NETSTANDARD2_0
            // RuntimeFeature.IsDynamicCodeSupported is not part of netstandard2.0, but the runtimes that load this
            // build (Unity, Xamarin, Mono) are the ones that need the answer.
            // This runs in a type initializer, where an exception would be cached and fail every later
            // expression. Stripped or partial reflection metadata is plausible on exactly these runtimes, so
            // any failure falls back to the behaviour before this probe existed.
            try
            {
                var property = typeof(object).Assembly
                    .GetType("System.Runtime.CompilerServices.RuntimeFeature")
                    ?.GetProperty("IsDynamicCodeSupported");

                return property == null || (bool)property.GetValue(null);
            }
            catch (Exception)
            {
                return true;
            }
#else
            return System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
#endif
        }

        /// <summary>
        /// The five values an expression can read, carried in one object.
        /// </summary>
        internal sealed class Arguments
        {
            public readonly IEnumerable<BsonDocument> Source;
            public readonly BsonDocument Root;
            public readonly BsonValue Current;
            public readonly Collation Collation;
            public readonly BsonDocument Parameters;

            public Arguments(IEnumerable<BsonDocument> source, BsonDocument root, BsonValue current, Collation collation, BsonDocument parameters)
            {
                Source = source;
                Root = root;
                Current = current;
                Collation = collation;
                Parameters = parameters;
            }
        }

        /// <summary>
        /// Replaces the five parameters of an ExpressionContext with reads from one <see cref="Arguments"/> parameter.
        /// </summary>
        private sealed class ArgumentRewriter : ExpressionVisitor
        {
            private readonly Dictionary<ParameterExpression, Expression> _replacements;

            public ArgumentRewriter(ExpressionContext context, ParameterExpression arguments)
            {
                _replacements = new Dictionary<ParameterExpression, Expression>
                {
                    [context.Source] = Expression.Field(arguments, typeof(Arguments).GetField(nameof(Arguments.Source))),
                    [context.Root] = Expression.Field(arguments, typeof(Arguments).GetField(nameof(Arguments.Root))),
                    [context.Current] = Expression.Field(arguments, typeof(Arguments).GetField(nameof(Arguments.Current))),
                    [context.Collation] = Expression.Field(arguments, typeof(Arguments).GetField(nameof(Arguments.Collation))),
                    [context.Parameters] = Expression.Field(arguments, typeof(Arguments).GetField(nameof(Arguments.Parameters)))
                };
            }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                return _replacements.TryGetValue(node, out var replacement) ? replacement : base.VisitParameter(node);
            }
        }
    }
}
