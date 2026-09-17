using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

namespace LiteDB
{
    internal static class RuntimeExpression
    {
        // Do not permit beforefieldinit to probe before the startup switch is set.
        static RuntimeExpression() { }

        private static readonly bool CanCompile = ProbeCompilation();
#if TESTING
        private static readonly AsyncLocal<bool> _forceInterpretation = new AsyncLocal<bool>();
        internal static bool ForceInterpretation { get => _forceInterpretation.Value; set => _forceInterpretation.Value = value; }
#endif
        internal static TDelegate Compile<TDelegate>(Expression<TDelegate> lambda)
        {
            var interpret = !CanCompile;
#if TESTING
            interpret |= ForceInterpretation;
#endif
            if (!interpret) return lambda.Compile();
            Func<object[], object> evaluate = arguments => ExpressionInterpreter.Evaluate(lambda.Body, lambda.Parameters, arguments);
            object result;
            if (typeof(TDelegate) == typeof(BsonExpressionScalarDelegate))
                result = new BsonExpressionScalarDelegate((source, root, current, collation, parameters) =>
                    (BsonValue)evaluate(new object[] { source, root, current, collation, parameters }));
            else if (typeof(TDelegate) == typeof(BsonExpressionEnumerableDelegate))
                result = new BsonExpressionEnumerableDelegate((source, root, current, collation, parameters) =>
                    (IEnumerable<BsonValue>)evaluate(new object[] { source, root, current, collation, parameters }));
            else if (typeof(TDelegate) == typeof(CreateObject))
                result = new CreateObject(document => evaluate(new object[] { document }));
            else if (typeof(TDelegate) == typeof(GenericGetter))
                result = new GenericGetter(target => evaluate(new[] { target }));
            else if (typeof(TDelegate) == typeof(GenericSetter))
                result = new GenericSetter((target, value) => evaluate(new[] { target, value }));
            else if (typeof(TDelegate) == typeof(Func<object>))
                result = new Func<object>(() => evaluate(Array.Empty<object>()));
            else throw new NotSupportedException("No AOT expression adapter for " + typeof(TDelegate));
            return (TDelegate)result;
        }

        internal static object InvokeCaptured(Func<object> function)
        {
            try { return function(); }
            catch (Exception error) { throw new TargetInvocationException(error); }
        }

        private static bool ProbeCompilation()
        {
            if (AppContext.TryGetSwitch("LiteDB.UseInterpreter", out var force) && force) return false;
#if NET8_0_OR_GREATER
            if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return false;
#else
            // RuntimeFeature is not part of netstandard2.0, but newer hosts can
            // expose its capability flag through their core library.
            var feature = typeof(object).Assembly.GetType("System.Runtime.CompilerServices.RuntimeFeature");
            var supported = feature?.GetProperty("IsDynamicCodeSupported", BindingFlags.Public | BindingFlags.Static);
            if (supported?.GetValue(null) is bool available && !available) return false;
#endif
            try
            {
                // Some Mono versions accept Compile but fail only when invoking
                // the generated method. Probe our custom delegates: built-in Func
                // signatures can work while custom interpreter thunks cannot.
                Expression<BsonExpressionScalarDelegate> scalar = (source, root, current, collation, parameters) => current;
                scalar.Compile()(null, null, null, null, null);
                Expression<BsonExpressionEnumerableDelegate> enumerable = (source, root, current, collation, parameters) => null;
                enumerable.Compile()(null, null, null, null, null);
                Expression<CreateObject> create = document => document;
                create.Compile()(null);
                Expression<GenericGetter> getter = target => target;
                getter.Compile()(null);
                var target = Expression.Parameter(typeof(object));
                var value = Expression.Parameter(typeof(object));
                Expression.Lambda<GenericSetter>(Expression.Empty(), target, value).Compile()(null, null);
                return Expression.Lambda<Func<object>>(Expression.Constant(null, typeof(object))).Compile()() == null;
            }
            catch (NotSupportedException) { return false; }
            catch (ExecutionEngineException) { return false; }
        }
    }
}
