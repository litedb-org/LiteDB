using System.Linq.Expressions;
using static LiteDB.BsonExpressionFactory;

namespace LiteDB
{
    internal static class LinqComparisonTranslator
    {
        internal static BsonExpression Translate(BinaryExpression node, string operation, BsonExpression left,
            BsonExpression right, ExpressionContext context, BsonDocument parameters)
        {
            var comparison = Group(Binary(operation, left, right, context, parameters));
            if (!node.IsLifted || (operation != "<" && operation != "<=" && operation != ">" &&
                operation != ">=" && operation != "=" && operation != "!="))
                return comparison;
            if (!node.IsLiftedToNull && (operation == "=" || operation == "!="))
                return comparison;

            // Arithmetic and calls must evaluate both operands exactly once, even when
            // one is null. A short-circuit guard could suppress an error or repeat NOW().
            if (!CanRepeat(left) || !CanRepeat(right))
                return Call("LIFTED_COMPARE", new[] { left, right, Constant(operation, parameters),
                    Constant(node.IsLiftedToNull, parameters) }, context, parameters);

            // Plain paths and bound values are safe to guard and retain the ordinary
            // relation as an index-planning term. Equality/inequality already have CLR
            // null semantics unless explicitly constructed with liftToNull: true.
            var nonNull = Group(Binary("AND",
                Group(Binary("!=", left, Constant(BsonValue.Null, parameters), context, parameters)),
                Group(Binary("!=", right, Constant(BsonValue.Null, parameters), context, parameters)),
                context, parameters));
            return node.IsLiftedToNull
                ? Call("IIF", new[] { nonNull, comparison, Constant(BsonValue.Null, parameters) }, context, parameters)
                : Group(Binary("AND", nonNull, comparison, context, parameters));
        }

        private static bool CanRepeat(BsonExpression expression) =>
            !expression.IsVolatile && (expression.Type == BsonExpressionType.Path ||
                expression.Type == BsonExpressionType.Parameter);
    }
}
