using System;
using System.Numerics;

namespace LiteDB
{
    internal static class BsonNumberComparison
    {
        internal static int GetHashCode(BsonValue value)
        {
            if (value.IsDouble && (double.IsNaN(value.AsDouble) || double.IsInfinity(value.AsDouble)))
                return double.IsNaN(value.AsDouble) ? double.NaN.GetHashCode() : value.AsDouble.GetHashCode();

            if (value.IsInt32 || value.IsInt64)
                return unchecked(new BigInteger(value.AsInt64).GetHashCode() * 397 ^ BigInteger.One.GetHashCode());

            GetFraction(value, out var numerator, out var denominator);
            var divisor = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
            numerator /= divisor;
            denominator /= divisor;
            return unchecked(numerator.GetHashCode() * 397 ^ denominator.GetHashCode());
        }

        private static void GetFraction(BsonValue value, out BigInteger numerator, out BigInteger denominator)
        {
            if (value.IsInt32 || value.IsInt64)
            {
                numerator = new BigInteger(value.AsInt64);
                denominator = BigInteger.One;
            }
            else if (value.IsDouble)
            {
                var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value.AsDouble));
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
            else
            {
                var parts = decimal.GetBits(Convert.ToDecimal(value.RawValue));
                numerator = new BigInteger((uint)parts[0]) +
                    (new BigInteger((uint)parts[1]) << 32) + (new BigInteger((uint)parts[2]) << 64);
                if (parts[3] < 0) numerator = -numerator;
                denominator = BigInteger.Pow(10, (parts[3] >> 16) & 0xff);
            }
        }

        internal static int Compare(BsonValue left, BsonValue right)
        {
            if (!left.IsDouble && !right.IsDouble)
                return Convert.ToDecimal(left.RawValue).CompareTo(Convert.ToDecimal(right.RawValue));
            if (left.IsDouble && right.IsDouble) return left.AsDouble.CompareTo(right.AsDouble);

            var doubleOnLeft = left.IsDouble;
            var number = doubleOnLeft ? left.AsDouble : right.AsDouble;
            int order;
            if (double.IsNaN(number) || double.IsNegativeInfinity(number)) order = -1;
            else if (double.IsPositiveInfinity(number)) order = 1;
            else
            {
                // Compare exact rational values: binary64 significand * 2^exponent
                // against the decimal coefficient / 10^scale. Neither side rounds.
                GetFraction(doubleOnLeft ? left : right, out var numerator, out var denominator);
                GetFraction(doubleOnLeft ? right : left, out var coefficient, out var decimalDenominator);
                order = (numerator * decimalDenominator).CompareTo(coefficient * denominator);
            }
            return doubleOnLeft ? order : -order;
        }
    }
}
