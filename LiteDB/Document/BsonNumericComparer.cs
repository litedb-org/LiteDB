using System;
using System.Numerics;

namespace LiteDB
{
    /// <summary>
    /// Compares numbers of different BSON types by their exact value, so that ordering and equality
    /// stay transitive no matter which types a comparison happens to mix.
    /// </summary>
    internal static class BsonNumericComparer
    {
        private const double MinLongAsDouble = -9223372036854775808d;
        private const double MaxLongAsDouble = 9223372036854775808d;

        // largest decimal mantissas that still leave room for one more doubling or one more
        // multiplication by five. Decimal.MaxValue / 2m and / 5m round up, which is one step too late.
        private const decimal HalfOfMaxMantissa = 39614081257132168796771975167m;
        private const decimal FifthOfMaxMantissa = 15845632502852867518708790067m;

        public static int Compare(BsonValue left, BsonValue right)
        {
            if (left.IsDouble)
            {
                if (right.IsDouble)
                {
                    return left.AsDouble.CompareTo(right.AsDouble);
                }

                return right.IsDecimal
                    ? -CompareDecimalToDouble(right.AsDecimal, left.AsDouble)
                    : -CompareInt64ToDouble(right.AsInt64, left.AsDouble);
            }

            if (right.IsDouble)
            {
                return left.IsDecimal
                    ? CompareDecimalToDouble(left.AsDecimal, right.AsDouble)
                    : CompareInt64ToDouble(left.AsInt64, right.AsDouble);
            }

            // decimal holds every Int32 and Int64 exactly
            if (left.IsDecimal || right.IsDecimal)
            {
                return left.AsDecimal.CompareTo(right.AsDecimal);
            }

            return left.AsInt64.CompareTo(right.AsInt64);
        }

        /// <summary>
        /// Compares an integer with a double without converting either one, so no precision is lost
        /// above 2^53 and NaN keeps the position Double.CompareTo gives it.
        /// </summary>
        public static int CompareInt64ToDouble(long left, double right)
        {
            if (Double.IsNaN(right)) return 1;
            if (right >= MaxLongAsDouble) return -1;
            if (right < MinLongAsDouble) return 1;

            var truncated = Math.Truncate(right);
            var rightInteger = (long) truncated;

            if (left != rightInteger) return left < rightInteger ? -1 : 1;

            var fraction = right - truncated;

            return fraction == 0d ? 0 : fraction > 0d ? -1 : 1;
        }

        /// <summary>
        /// Compares a decimal with a double as exact fractions. A double is a multiple of a power of
        /// two and a decimal a multiple of a power of ten, so neither converts into the other without
        /// rounding, and rounding is what breaks transitivity.
        /// </summary>
        public static int CompareDecimalToDouble(decimal left, double right)
        {
            if (Double.IsNaN(right)) return 1;
            if (Double.IsPositiveInfinity(right)) return -1;
            if (Double.IsNegativeInfinity(right)) return 1;

            if (right >= (double) Decimal.MaxValue) return -1;
            if (right <= (double) Decimal.MinValue) return 1;

            GetExactValue(left, out var leftNumerator, out var leftDenominator);
            GetExactValue(right, out var rightNumerator, out var rightDenominator);

            return (leftNumerator * rightDenominator).CompareTo(rightNumerator * leftDenominator);
        }

        /// <summary>
        /// The decimal holding exactly the same value as <paramref name="value"/>, when one exists.
        /// A double that no decimal can hold cannot equal a decimal or an integer either, so hashing
        /// it separately keeps equal values on the same hash.
        /// </summary>
        public static bool TryGetExactDecimal(double value, out decimal result)
        {
            result = 0m;

            if (Double.IsNaN(value) || Double.IsInfinity(value)) return false;

            var bits = BitConverter.DoubleToInt64Bits(value);
            var exponent = (int) ((bits >> 52) & 0x7FF);
            var mantissa = bits & 0xFFFFFFFFFFFFFL;

            if (exponent == 0)
            {
                exponent = 1;
            }
            else
            {
                mantissa |= 1L << 52;
            }

            exponent -= 1075;

            // drop the trailing zero bits, so that a value such as 1.0 is 1 * 2^0 and not 2^52 * 2^-52
            while (exponent < 0 && (mantissa & 1L) == 0)
            {
                mantissa >>= 1;
                exponent++;
            }

            decimal magnitude = mantissa;
            var scale = 0;

            if (exponent >= 0)
            {
                for (var i = 0; i < exponent; i++)
                {
                    if (magnitude > HalfOfMaxMantissa) return false;

                    magnitude *= 2m;
                }
            }
            else
            {
                // value = mantissa / 2^k = mantissa * 5^k / 10^k, and decimal holds at most 28 decimals
                scale = -exponent;

                if (scale > 28) return false;

                for (var i = 0; i < scale; i++)
                {
                    if (magnitude > FifthOfMaxMantissa) return false;

                    magnitude *= 5m;
                }
            }

            var magnitudeBits = Decimal.GetBits(magnitude);

            result = new Decimal(magnitudeBits[0], magnitudeBits[1], magnitudeBits[2], bits < 0, (byte) scale);
            return true;
        }

        /// <summary>
        /// Hash code shared by every numeric type that holds the same value.
        /// </summary>
        public static int GetHashCode(BsonValue value)
        {
            if (value.IsDouble)
            {
                var number = value.AsDouble;

                return TryGetExactDecimal(number, out var exact) ? exact.GetHashCode() : number.GetHashCode();
            }

            return value.AsDecimal.GetHashCode();
        }

        private static void GetExactValue(decimal value, out BigInteger numerator, out BigInteger denominator)
        {
            var bits = Decimal.GetBits(value);

            var mantissa = new BigInteger((uint) bits[0]);
            mantissa |= (BigInteger) (uint) bits[1] << 32;
            mantissa |= (BigInteger) (uint) bits[2] << 64;

            numerator = (bits[3] & Int32.MinValue) != 0 ? -mantissa : mantissa;
            denominator = BigInteger.Pow(10, (bits[3] >> 16) & 0xFF);
        }

        private static void GetExactValue(double value, out BigInteger numerator, out BigInteger denominator)
        {
            var bits = BitConverter.DoubleToInt64Bits(value);
            var exponent = (int) ((bits >> 52) & 0x7FF);
            var mantissa = bits & 0xFFFFFFFFFFFFFL;

            if (exponent == 0)
            {
                exponent = 1;
            }
            else
            {
                mantissa |= 1L << 52;
            }

            exponent -= 1075;

            var result = bits < 0 ? -new BigInteger(mantissa) : new BigInteger(mantissa);

            if (exponent >= 0)
            {
                numerator = result << exponent;
                denominator = BigInteger.One;
            }
            else
            {
                numerator = result;
                denominator = BigInteger.One << -exponent;
            }
        }
    }
}
