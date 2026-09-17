using System;
using System.Linq.Expressions;
using System.Threading;

namespace LiteDB
{
    public sealed partial class BsonExpression
    {
        private static BsonExpressionScalarDelegate CompileScalarWhenNeeded(BsonExpression expression,
            Expression<BsonExpressionScalarDelegate> lambda)
        {
            var sourceText = expression.Source;
            if (expression.Type != BsonExpressionType.And && expression.Type != BsonExpressionType.Or)
                return _compiledCache.Add(sourceText, RuntimeExpression.Compile(lambda));

            // Unexecuted predicates own their factory, outside the global cache.
            // Otherwise discarded composition prefixes retain entire parse trees.
            var interpret = RuntimeExpression.UseInterpretation;
            var cacheable = true;
#if TESTING
            cacheable = !RuntimeExpression.ForceInterpretation;
#endif
            var compiled = new Lazy<BsonExpressionScalarDelegate>(() =>
            {
                var cached = cacheable ? _compiledCache.Get<BsonExpressionScalarDelegate>(sourceText) : null;
                if (cached != null) return cached;
                var result = RuntimeExpression.Compile(lambda, interpret);
                return cacheable ? _compiledCache.Add(sourceText, result) : result;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            return (source, root, current, collation, parameters) =>
                compiled.Value(source, root, current, collation, parameters);
        }
    }
}
