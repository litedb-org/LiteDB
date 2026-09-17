using System;

namespace LiteDB
{
    // Receivers come from stored documents: a null, missing or non-string field
    // must not match, because throwing would abort the whole query or DeleteMany.
    internal partial class BsonExpressionMethods
    {
        /// <summary>Compare strings with an explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_EQUALS(BsonValue left, BsonValue right, BsonValue mode)
        {
            var comparison = ParseStringComparison(mode);
            if (!(left.IsString || left.IsNull) || !(right.IsString || right.IsNull)) return false;
            return string.Equals(left.IsNull ? null : left.AsString, right.IsNull ? null : right.AsString, comparison);
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

        /// <summary>Compare an instance string; a receiver that is not a string never matches.</summary>
        [Volatile]
        public static BsonValue STRING_EQUALS_INSTANCE(BsonValue left, BsonValue right, BsonValue mode)
        {
            var result = STRING_EQUALS(left, right, mode);
            return left.IsString ? result : false;
        }

        /// <summary>Test a prefix using the explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_STARTSWITH(BsonValue value, BsonValue text, BsonValue mode)
        {
            var comparison = ParseStringComparison(mode);
            return value.IsString && value.AsString.StartsWith(text.AsString, comparison);
        }

        /// <summary>Test a suffix using the explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_ENDSWITH(BsonValue value, BsonValue text, BsonValue mode)
        {
            var comparison = ParseStringComparison(mode);
            return value.IsString && value.AsString.EndsWith(text.AsString, comparison);
        }

        /// <summary>Test a literal substring using the explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_CONTAINS(BsonValue value, BsonValue text, BsonValue mode)
        {
            var comparison = ParseStringComparison(mode);
            return value.IsString && value.AsString.IndexOf(text.AsString, comparison) >= 0;
        }

        /// <summary>Find a literal substring using the explicit CLR comparison mode (null, like INDEXOF, without a string receiver).</summary>
        [Volatile]
        public static BsonValue STRING_INDEXOF(BsonValue value, BsonValue text, BsonValue mode)
        {
            var comparison = ParseStringComparison(mode);
            return value.IsString ? value.AsString.IndexOf(text.AsString, comparison) : BsonValue.Null;
        }

        /// <summary>Find a substring from a start index using the explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_INDEXOF(BsonValue value, BsonValue text, BsonValue start, BsonValue mode)
        {
            var comparison = ParseStringComparison(mode);
            return value.IsString ? value.AsString.IndexOf(text.AsString, start.AsInt32, comparison) : BsonValue.Null;
        }

        /// <summary>Find a substring in a range using the explicit CLR comparison mode.</summary>
        [Volatile]
        public static BsonValue STRING_INDEXOF(BsonValue value, BsonValue text, BsonValue start, BsonValue count, BsonValue mode)
        {
            var comparison = ParseStringComparison(mode);
            return value.IsString ? value.AsString.IndexOf(text.AsString, start.AsInt32, count.AsInt32, comparison) : BsonValue.Null;
        }
    }
}
