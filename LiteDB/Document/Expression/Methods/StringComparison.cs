using System;

namespace LiteDB
{
    internal partial class BsonExpressionMethods
    {
        /// <summary>Compare strings with an explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_EQUALS(BsonValue left, BsonValue right, BsonValue mode)
        {
            // CurrentCulture modes depend on the executing thread and must not
            // become persistent expression indexes or constant-folded values.
            StringComparison comparison;
            if (mode.IsString)
            {
                if (!Enum.TryParse(mode.AsString, out comparison))
                    throw new ArgumentException("Invalid StringComparison value.", nameof(mode));
            }
            else if (mode.IsInt32) comparison = (StringComparison)mode.AsInt32;
            else throw new ArgumentException("Invalid StringComparison value.", nameof(mode));

            if (comparison < StringComparison.CurrentCulture || comparison > StringComparison.OrdinalIgnoreCase)
                throw new ArgumentException("Invalid StringComparison value.", nameof(mode));
            return string.Equals(left.IsNull ? null : left.AsString, right.IsNull ? null : right.AsString, comparison);
        }

        /// <summary>Compare an instance string with the CLR null-receiver behavior.</summary>
        [Volatile]
        public static BsonValue STRING_EQUALS_INSTANCE(BsonValue left, BsonValue right, BsonValue mode)
        {
            if (left.IsNull) throw new NullReferenceException("String.Equals requires a non-null receiver.");
            return STRING_EQUALS(left, right, mode);
        }
    }
}
