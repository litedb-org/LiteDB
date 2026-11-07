namespace LiteDB.Engine
{
    internal static class PrimaryKeyDeleteRange
    {
        internal static bool TryCreate(BsonExpression predicate, Collation collation, out IndexRange range)
        {
            range = null;
            if (!HasShape(predicate)) return false;
            var type = predicate.Type;
            var bound = predicate.Right.ExecuteScalar(collation);
            // Keep container/null/sentinel semantics on the existing query path.
            if (bound.IsArray || bound.IsDocument || bound.IsNull || bound.IsMinValue || bound.IsMaxValue) return false;
            var upper = type == BsonExpressionType.LessThan || type == BsonExpressionType.LessThanOrEqual;
            var inclusive = type == BsonExpressionType.LessThanOrEqual || type == BsonExpressionType.GreaterThanOrEqual;
            range = new IndexRange("_id", upper ? BsonValue.MinValue : bound,
                upper ? bound : BsonValue.MaxValue, !upper && inclusive, upper && inclusive, Query.Ascending);
            return true;
        }

        internal static bool HasShape(BsonExpression predicate)
        {
            if (predicate?.Left?.Type != BsonExpressionType.Path || predicate.Left.Source != "$._id") return false;
            var type = predicate.Type;
            if (type != BsonExpressionType.LessThan && type != BsonExpressionType.LessThanOrEqual &&
                type != BsonExpressionType.GreaterThan && type != BsonExpressionType.GreaterThanOrEqual) return false;
            var value = predicate.Right;
            if (value == null || !value.IsScalar || !value.IsValue || value.UseSource || value.IsVolatile ||
                (!value.IsImmutable && value.Type != BsonExpressionType.Parameter)) return false;
            return true;
        }
    }
}
