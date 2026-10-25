using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace LiteDB
{
    // The semantic construction boundary shared by the text and LINQ frontends.
    internal static partial class BsonExpressionFactory
    {
        internal static BsonExpression Binary(string operation, BsonExpression left, BsonExpression right,
            ExpressionContext context, BsonDocument parameters)
        {
            var op = Operators[operation];
            var src = op.Item1;
            var method = op.Item2;
            var type = op.Item3;

            // test left/right scalar
            var isLeftEnum = operation.StartsWith("ALL") || operation.StartsWith("ANY");

            if (isLeftEnum && left.IsScalar) left = ConvertToEnumerable(left);
            //if (isLeftEnum && left.IsScalar) throw new LiteException(0, $"Left expression `{left.Source}` must return multiples values");
            if (!isLeftEnum && !left.IsScalar) throw new LiteException(0, $"Left expression `{left.Source}` returns more than one result. Try use ANY or ALL before operant.");
            if (right.IsScalar == false) throw new LiteException(0, $"Right expression `{right.Source}` must return a single value");

            BsonExpression result;

            // when operation is AND/OR, use AndAlso|OrElse
            if (type == BsonExpressionType.And || type == BsonExpressionType.Or)
            {
                result = CreateLogicExpression(type, left, right);
            }
            else
            {
                // method call parameters
                var args = new List<Expression>();

                if (method?.GetParameters().FirstOrDefault()?.ParameterType == typeof(Collation))
                {
                    args.Add(context.Collation);
                }

                args.Add(left.Expression);
                args.Add(right.Expression);

                // process result in a single value
                result = new BsonExpression
                {
                    Type = type,
                    Parameters = parameters,
                    IsImmutable = left.IsImmutable && right.IsImmutable,
                    IsVolatile = left.IsVolatile || right.IsVolatile,
                    UseSource = left.UseSource || right.UseSource,
                    IsScalar = true,
                    IsANY = operation.StartsWith("ANY", StringComparison.Ordinal),
                    Fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase).AddRange(left.Fields).AddRange(right.Fields),
                    Expression = Expression.Call(method, args.ToArray()),
                    Left = left,
                    Right = right,
                    Source = BsonExpressionFormatter.Binary(src, left, right)
                };
            }

            return result;
        }
    }
}
