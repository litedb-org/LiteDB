using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB
{
    public sealed partial class BsonExpression
    {
        /// <summary>
        /// Reuse this compiled expression with a separate parameter document, without
        /// parsing or translating it again. Parameter names are the keys in Parameters.
        /// The supplied document must not be modified while the bound expression executes.
        /// </summary>
        public BsonExpression Bind(BsonDocument parameters)
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            return new BsonExpression
            {
                Source = Source, Type = Type, IsImmutable = IsImmutable,
                Parameters = parameters, Left = Left?.Bind(parameters), Right = Right?.Bind(parameters),
                UseSource = UseSource, Expression = Expression, IsScalar = IsScalar,
                Fields = new HashSet<string>(Fields, StringComparer.OrdinalIgnoreCase),
                _funcScalar = _funcScalar, _funcEnumerable = _funcEnumerable
            };
        }

        // Compose already compiled expressions in a new construction context. The
        // delegate accepts parameter values at execution time, including nested lambdas.
        internal Expression Invoke(ExpressionContext context)
        {
            var function = IsScalar ? (Delegate)_funcScalar : _funcEnumerable;
            return System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(function),
                context.Source, context.Root, context.Current, context.Collation, context.Parameters);
        }
    }
}
