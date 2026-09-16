namespace LiteDB.Engine
{
    // Intersects scalar predicates using BSON ordering and the active database collation.
    internal sealed class ScalarBounds
    {
        internal BsonValue Lower { get; private set; }
        internal BsonValue Upper { get; private set; }
        internal bool LowerInclusive { get; private set; }
        internal bool UpperInclusive { get; private set; }

        internal ScalarBounds(BsonValue lower, BsonValue upper, bool lowerInclusive, bool upperInclusive)
        {
            Lower = lower;
            Upper = upper;
            LowerInclusive = lowerInclusive;
            UpperInclusive = upperInclusive;
        }

        internal void Intersect(BsonExpressionType operation, BsonValue value, Collation collation)
        {
            if (operation == BsonExpressionType.Equal || operation == BsonExpressionType.GreaterThan ||
                operation == BsonExpressionType.GreaterThanOrEqual)
            {
                var compare = value.CompareTo(Lower, collation);
                var inclusive = operation != BsonExpressionType.GreaterThan;
                if (compare > 0) { Lower = value; LowerInclusive = inclusive; }
                else if (compare == 0) LowerInclusive &= inclusive;
            }
            if (operation == BsonExpressionType.Equal || operation == BsonExpressionType.LessThan ||
                operation == BsonExpressionType.LessThanOrEqual)
            {
                var compare = value.CompareTo(Upper, collation);
                var inclusive = operation != BsonExpressionType.LessThan;
                if (compare < 0) { Upper = value; UpperInclusive = inclusive; }
                else if (compare == 0) UpperInclusive &= inclusive;
            }
        }

        internal bool IsEmpty(Collation collation)
        {
            var compare = Lower.CompareTo(Upper, collation);
            return compare > 0 || (compare == 0 && !(LowerInclusive && UpperInclusive));
        }
    }
}
