using System;

namespace LiteDB
{
    internal partial class BsonExpressionMethods
    {
        /// <summary>Compare strings with an explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_EQUALS(BsonValue left, BsonValue right, BsonValue mode)
        {
            return string.Equals(left.IsNull ? null : left.AsString, right.IsNull ? null : right.AsString, ParseStringComparison(mode));
        }

        private static StringComparison ParseStringComparison(BsonValue mode)
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
            return comparison;
        }

        /// <summary>Compare an instance string with the CLR null-receiver behavior.</summary>
        [Volatile]
        public static BsonValue STRING_EQUALS_INSTANCE(BsonValue left, BsonValue right, BsonValue mode)
        {
            if (left.IsNull) throw new NullReferenceException("String.Equals requires a non-null receiver.");
            return STRING_EQUALS(left, right, mode);
        }

        private static string StringReceiver(BsonValue value)
        {
            if (value.IsNull) throw new NullReferenceException("String method requires a non-null receiver.");
            return value.AsString;
        }

        /// <summary>Test a prefix using the explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_STARTSWITH(BsonValue value, BsonValue text, BsonValue mode) =>
            StringReceiver(value).StartsWith(text.AsString, ParseStringComparison(mode));

        /// <summary>Test a suffix using the explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_ENDSWITH(BsonValue value, BsonValue text, BsonValue mode) =>
            StringReceiver(value).EndsWith(text.AsString, ParseStringComparison(mode));

        /// <summary>Test a literal substring using the explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_CONTAINS(BsonValue value, BsonValue text, BsonValue mode) =>
            StringReceiver(value).IndexOf(text.AsString, ParseStringComparison(mode)) >= 0;

        /// <summary>Find a literal substring using the explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_INDEXOF(BsonValue value, BsonValue text, BsonValue mode) =>
            StringReceiver(value).IndexOf(text.AsString, ParseStringComparison(mode));

        /// <summary>Find a substring from a start index using the explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_INDEXOF(BsonValue value, BsonValue text, BsonValue start, BsonValue mode) =>
            StringReceiver(value).IndexOf(text.AsString, start.AsInt32, ParseStringComparison(mode));

        /// <summary>Find a substring in a range using the explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_INDEXOF(BsonValue value, BsonValue text, BsonValue start, BsonValue count, BsonValue mode) =>
            StringReceiver(value).IndexOf(text.AsString, start.AsInt32, count.AsInt32, ParseStringComparison(mode));
    }
}
