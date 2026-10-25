using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace LiteDB
{
    internal static partial class BsonExpressionFactory
    {
        internal static BsonExpression FilterPath(BsonExpression target, BsonExpression filter, ExpressionContext context)
        {
            var result = Copy(target);
            result.IsScalar = false;
            result.Expression = Expression.Call(_arrayFilterMethod, target.Expression,
                Expression.Constant(filter == null ? int.MaxValue : 0), NestedTemplate(filter),
                context.Root, context.Collation, context.Parameters);
            result.Source = target.Source + "[" + (filter?.Source ?? "*") + "]";
            if (filter != null)
            {
                result.IsImmutable &= filter.IsImmutable;
                result.IsVolatile |= filter.IsVolatile;
                result.UseSource |= filter.UseSource;
                result.Fields = new HashSet<string>(target.Fields, StringComparer.OrdinalIgnoreCase).AddRange(filter.Fields);
            }
            return result;
        }

        // Path shortcuts have historically tracked the lambda's metadata, whereas
        // explicit MAP/FILTER syntax tracks the input. Keep that distinction here.
        internal static BsonExpression MapPath(BsonExpression target, BsonExpression selector, ExpressionContext context)
        {
            var result = Function("MAP", BsonExpressionType.Map, target, selector,
                new BsonExpression[0], context, target.Parameters);
            result.IsImmutable &= selector.IsImmutable;
            result.UseSource |= selector.UseSource;
            if (target.Type == BsonExpressionType.Source) result.Fields = new HashSet<string>(selector.Fields, StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }
}
