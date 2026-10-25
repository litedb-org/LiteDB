using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace LiteDB
{
    internal static partial class BsonExpressionFactory
    {
        internal static BsonExpression Function(string name, BsonExpressionType type,
            BsonExpression left, BsonExpression right, IList<BsonExpression> arguments,
            ExpressionContext context, BsonDocument parameters,
            bool convertScalarLeftToEnumerable = true, bool isScalarResult = false)
        {
            if (convertScalarLeftToEnumerable && left.IsScalar) left = ConvertToEnumerable(left);
            var args = new List<Expression> { context.Root, context.Collation, context.Parameters, left.Expression };
            var fields = new HashSet<string>(left.Fields, StringComparer.OrdinalIgnoreCase);
            var isImmutable = left.IsImmutable;
            var isVolatile = left.IsVolatile;
            var useSource = left.UseSource;
            if (right != null)
            {
                args.Add(NestedTemplate(right));
                fields.AddRange(right.Fields);
                isVolatile |= right.IsVolatile;
            }
            foreach (var argument in arguments)
            {
                args.Add(argument.Expression);
                isImmutable &= argument.IsImmutable;
                isVolatile |= argument.IsVolatile;
                useSource |= argument.UseSource;
                fields.AddRange(argument.Fields);
            }
            if (type == BsonExpressionType.VectorSim && (args.Count != 5 || arguments.Count != 1))
            {
                throw new LiteException(LiteException.UNEXPECTED_TOKEN, "VECTOR_SIM requires exactly two arguments.");
            }
            var method = BsonExpression.GetFunction(name, args.Count - 5);
            return new BsonExpression
            {
                Type = type,
                Left = type == BsonExpressionType.VectorSim ? left : null,
                Right = type == BsonExpressionType.VectorSim ? arguments[0] : null,
                Parameters = parameters, IsImmutable = isImmutable, UseSource = useSource, IsVolatile = isVolatile,
                IsScalar = isScalarResult, Fields = fields,
                Expression = Expression.Call(method, args), Source = BsonExpressionFormatter.Function(name, left, right, arguments)
            };
        }

        private static ConstantExpression NestedTemplate(BsonExpression expression)
        {
            // Nested delegates receive current parameters explicitly. Their cached
            // templates must not retain the first caller's bound payloads.
            return Expression.Constant(expression?.WithoutParameters() ?? new BsonExpression());
        }
    }
}
