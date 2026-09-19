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
                return _compiledCache.Add(sourceText, lambda.Compile());

            // Unexecuted predicates own their factory, outside the global cache.
            // Otherwise discarded composition prefixes retain entire parse trees.
            var compiled = new Lazy<BsonExpressionScalarDelegate>(() =>
            {
                var cached = _compiledCache.Get<BsonExpressionScalarDelegate>(sourceText);
                if (cached != null) return cached;
                return _compiledCache.Add(sourceText, lambda.Compile());
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            return (source, root, current, collation, parameters) =>
                compiled.Value(source, root, current, collation, parameters);
        }
    }
}
