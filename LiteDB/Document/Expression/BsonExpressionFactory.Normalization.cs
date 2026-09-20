namespace LiteDB
{
    internal static partial class BsonExpressionFactory
    {
        internal static BsonExpression RebuildLogical(BsonExpression original, BsonExpression left, BsonExpression right)
        {
            var context = new ExpressionContext();
            var lhs = Copy(left);
            var rhs = Copy(right);
            lhs.Expression = left.Invoke(context);
            rhs.Expression = right.Invoke(context);
            var result = Group(Binary(original.Type == BsonExpressionType.And ? "AND" : "OR", lhs, rhs, context, original.Parameters));
            BsonExpression.Compile(result, context);
            return result;
        }

        // Shared optimizer rewrite: values ANY = field becomes field IN ARRAY(values).
        // It must not send a structured query back through the textual frontend.
        internal static BsonExpression NormalizeContains(BsonExpression predicate)
        {
            var context = new ExpressionContext();
            var left = Copy(predicate.Right);
            var right = Copy(predicate.Left);
            left.Expression = predicate.Right.Invoke(context);
            right.Expression = predicate.Left.Invoke(context);
            var result = Binary("IN", left, ConvertToArray(right), context, predicate.Parameters);
            BsonExpression.Compile(result, context);
            return result;
        }
    }
}
