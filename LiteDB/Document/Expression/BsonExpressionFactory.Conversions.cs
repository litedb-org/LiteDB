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
        private static readonly MethodInfo _booleanValueMethod =
            typeof(BsonExpressionBoolean).GetMethod(nameof(BsonExpressionBoolean.FromBoolean));

        /// <summary>
        /// Convert scalar expression into enumerable expression using ITEMS(...) method
        /// Append [*] to path or ITEMS(..) in all others
        /// </summary>
        internal static BsonExpression ConvertToEnumerable(BsonExpression expr)
        {
            var src = expr.Type == BsonExpressionType.Path ?
                expr.Source + "[*]" :
                "ITEMS(" + expr.Source + ")";

            var exprType = expr.Type == BsonExpressionType.Path ?
                BsonExpressionType.Path :
                BsonExpressionType.Call;

            return new BsonExpression
            {
                Type = exprType,
                Parameters = expr.Parameters,
                IsImmutable = expr.IsImmutable, IsVolatile = expr.IsVolatile,
                UseSource = expr.UseSource,
                IsScalar = false,
                Fields = expr.Fields,
                Expression = Expression.Call(_itemsMethod, expr.Expression),
                Source = src
            };
        }

        /// <summary>
        /// Convert enumerable expression into array using ARRAY(...) method
        /// </summary>
        internal static BsonExpression ConvertToArray(BsonExpression expr)
        {
            return new BsonExpression
            {
                Type = BsonExpressionType.Call,
                Parameters = expr.Parameters,
                IsImmutable = expr.IsImmutable, IsVolatile = expr.IsVolatile,
                UseSource = expr.UseSource,
                IsScalar = true,
                Fields = expr.Fields,
                Expression = Expression.Call(_arrayMethod, expr.Expression),
                Source = "ARRAY(" + expr.Source + ")"
            };
        }

        /// <summary>
        /// Create new logic (AND/OR) expression based in 2 expressions
        /// </summary>
        internal static BsonExpression CreateLogicExpression(BsonExpressionType type, BsonExpression left, BsonExpression right)
        {
            // convert BsonValue into Boolean
            var boolLeft = Expression.Property(left.Expression, typeof(BsonValue), "AsBoolean");
            var boolRight = Expression.Property(right.Expression, typeof(BsonValue), "AsBoolean");

            var expr = type == BsonExpressionType.And ?
                Expression.AndAlso(boolLeft, boolRight) :
                Expression.OrElse(boolLeft, boolRight);

            // create new binary expression based in 2 other expressions
            var result = new BsonExpression
            {
                Type = type,
                Parameters = left.Parameters, // should be == right.Parameters
                IsImmutable = left.IsImmutable && right.IsImmutable,
                IsVolatile = left.IsVolatile || right.IsVolatile,
                UseSource = left.UseSource || right.UseSource,
                IsScalar = left.IsScalar && right.IsScalar,
                Fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase).AddRange(left.Fields).AddRange(right.Fields),
                Expression = Expression.Call(_booleanValueMethod, expr),
                Left = left,
                Right = right,
                Source = BsonExpressionFormatter.Binary(" " + type.ToString().ToUpperInvariant() + " ", left, right)
            };

            return result;
        }

        /// <summary>
        /// Create new conditional (IIF) expression. Execute expression only if True or False value
        /// </summary>
        internal static BsonExpression CreateConditionalExpression(BsonExpression test, BsonExpression ifTrue, BsonExpression ifFalse)
        {
            // convert BsonValue into Boolean
            var boolTest = Expression.Property(test.Expression, typeof(BsonValue), "AsBoolean");

            var expr = Expression.Condition(boolTest, ifTrue.Expression, ifFalse.Expression);

            // create new binary expression based in 2 other expressions
            var result = new BsonExpression
            {
                Type = BsonExpressionType.Call, // there is not specific Conditional
                Parameters = test.Parameters, // should be == ifTrue|ifFalse parameters
                IsImmutable = test.IsImmutable && ifTrue.IsImmutable && ifFalse.IsImmutable,
                IsVolatile = test.IsVolatile || ifTrue.IsVolatile || ifFalse.IsVolatile,
                UseSource = test.UseSource || ifTrue.UseSource || ifFalse.UseSource,
                IsScalar = test.IsScalar && ifTrue.IsScalar && ifFalse.IsScalar,
                Fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase).AddRange(test.Fields).AddRange(ifTrue.Fields).AddRange(ifFalse.Fields),
                Expression = expr,
                Source = BsonExpressionFormatter.Call("IIF", new[] { test, ifTrue, ifFalse })
            };

            return result;
        }
    }
}
