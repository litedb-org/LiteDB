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
            return BindCore(parameters);
        }

        // Embedded evaluators always receive parameters from their caller. Keeping
        // no fallback document also preserves errors for an explicitly null binding.
        internal BsonExpression WithoutParameters() => BindCore(null);

        private BsonExpression BindCore(BsonDocument parameters)
        {
            return new BsonExpression
            {
                Source = Source, Type = Type, IsImmutable = IsImmutable, IsVolatile = IsVolatile,
                Parameters = parameters, Left = Left?.BindCore(parameters), Right = Right?.BindCore(parameters),
                UseSource = UseSource, Expression = Expression, IsScalar = IsScalar, IsANY = IsANY,
                RequiresExactSort = RequiresExactSort,
                Fields = new HashSet<string>(Fields, StringComparer.OrdinalIgnoreCase),
                // Grouping fills these renamed @key parameters on the bound copy.
                GroupKeyAliases = GroupKeyAliases == null ? null : new HashSet<string>(GroupKeyAliases, GroupKeyAliases.Comparer),
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
