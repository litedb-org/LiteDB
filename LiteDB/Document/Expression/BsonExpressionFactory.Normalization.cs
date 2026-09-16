namespace LiteDB
{
    internal static partial class BsonExpressionFactory
    {
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
