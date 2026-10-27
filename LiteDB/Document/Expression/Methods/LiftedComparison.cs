using System;

namespace LiteDB
{
    internal partial class BsonExpressionMethods
    {
        /// <summary>
        /// Compare lifted operands once, applying CLR null lifting before BSON ordering.
        /// </summary>
        public static BsonValue LIFTED_COMPARE(Collation collation, BsonValue left,
            BsonValue right, BsonValue operation, BsonValue liftToNull)
        {
            if (left.IsNull || right.IsNull)
                return liftToNull.AsBoolean ? BsonValue.Null : BsonExpressionBoolean.FromBoolean(false);

            switch (operation.AsString)
            {
                case "<": return BsonExpressionOperators.LT(collation, left, right);
                case "<=": return BsonExpressionOperators.LTE(collation, left, right);
                case ">": return BsonExpressionOperators.GT(collation, left, right);
                case ">=": return BsonExpressionOperators.GTE(collation, left, right);
                case "=": return BsonExpressionOperators.EQ(collation, left, right);
                case "!=": return BsonExpressionOperators.NEQ(collation, left, right);
                default: throw new NotSupportedException("Unsupported lifted comparison: " + operation.AsString);
            }
        }
    }
}
