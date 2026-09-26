using System;
using System.Numerics;

namespace LiteDB
{
    /// <summary>
    /// Exact ordering, equality and hashing of mixed BSON numeric types. Every
    /// evaluator (owned values, borrowed scalars, index keys) must use this class
    /// so index seeks, scans and expressions agree.
    /// </summary>
    internal static class BsonNumberComparison
    {
        private const decimal MaxExactDouble = 9007199254740992m;

        internal static int GetHashCode(BsonValue value)
        {
            if (value.IsDouble && (double.IsNaN(value.AsDouble) || double.IsInfinity(value.AsDouble)))
                return double.IsNaN(value.AsDouble) ? double.NaN.GetHashCode() : value.AsDouble.GetHashCode();

            if (value.IsInt32 || value.IsInt64)
                return unchecked(new BigInteger(value.AsInt64).GetHashCode() * 397 ^ BigInteger.One.GetHashCode());

            BigInteger numerator, denominator;
            if (value.IsDouble) GetFraction(value.AsDouble, out numerator, out denominator);
            else GetFraction(Convert.ToDecimal(value.RawValue), out numerator, out denominator);
            var divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            numerator /= divisor;
            denominator /= divisor;
            return unchecked(numerator.GetHashCode() * 397 ^ denominator.GetHashCode());
        }

        internal static int Compare(BsonValue left, BsonValue right)
        {
            return Compare(left.IsDouble, left.IsDouble ? left.AsDouble : 0d, left.IsDouble ? 0m : Convert.ToDecimal(left.RawValue),
                right.IsDouble, right.IsDouble ? right.AsDouble : 0d, right.IsDouble ? 0m : Convert.ToDecimal(right.RawValue));
        }

        /// <summary>
        /// Orders two numbers by their exact values. A non-double operand is an Int32,
        /// Int64 or Decimal, all of which a decimal holds exactly. NaN and negative
        /// infinity sort below every number, positive infinity above every number.
        /// </summary>
        internal static int Compare(bool leftIsDouble, double leftDouble, decimal leftExact,
            bool rightIsDouble, double rightDouble, decimal rightExact)
        {
            if (!leftIsDouble && !rightIsDouble) return leftExact.CompareTo(rightExact);
            if (leftIsDouble && rightIsDouble) return leftDouble.CompareTo(rightDouble);
            return leftIsDouble ? CompareDouble(leftDouble, rightExact) : -CompareDouble(rightDouble, leftExact);
        }

        private static int CompareDouble(double number, decimal exact)
        {
            if (double.IsNaN(number) || double.IsNegativeInfinity(number)) return -1;
            if (double.IsPositiveInfinity(number)) return 1;

            // An integral decimal of magnitude at most 2^53 converts to double exactly.
            if (exact == decimal.Truncate(exact) && exact >= -MaxExactDouble && exact <= MaxExactDouble)
                return number.CompareTo((double)exact);

            // Compare exact rational values: binary64 significand * 2^exponent
            // against the decimal coefficient / 10^scale. Neither side rounds.
            GetFraction(number, out var numerator, out var denominator);
            GetFraction(exact, out var coefficient, out var decimalDenominator);
            return (numerator * decimalDenominator).CompareTo(coefficient * denominator);
        }

        private static void GetFraction(double value, out BigInteger numerator, out BigInteger denominator)
        {
            var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
            var exponentBits = (int)((bits >> 52) & 0x7ff);
            var significand = bits & 0x000fffffffffffffUL;
            if (exponentBits != 0) significand |= 1UL << 52;
            var exponent = exponentBits == 0 ? -1074 : exponentBits - 1023 - 52;
            numerator = new BigInteger(significand);
            if ((bits >> 63) != 0) numerator = -numerator;
            denominator = BigInteger.One;
            if (exponent >= 0) numerator <<= exponent;
            else denominator <<= -exponent;
        }

        private static void GetFraction(decimal value, out BigInteger numerator, out BigInteger denominator)
        {
            var parts = decimal.GetBits(value);
            numerator = new BigInteger((uint)parts[0]) +
                (new BigInteger((uint)parts[1]) << 32) + (new BigInteger((uint)parts[2]) << 64);
            if (parts[3] < 0) numerator = -numerator;
            denominator = BigInteger.Pow(10, (parts[3] >> 16) & 0xff);
        }
    }
}
