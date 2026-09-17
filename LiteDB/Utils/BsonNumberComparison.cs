using System;

namespace LiteDB
{
    internal static class BsonNumberComparison
    {
        internal static int Compare(BsonValue left, BsonValue right)
        {
            try
            {
                // Preserve established comparisons throughout the decimal domain.
                return Convert.ToDecimal(left.RawValue).CompareTo(Convert.ToDecimal(right.RawValue));
            }
            catch (OverflowException)
            {
                // Types differ, so exactly one operand is a double outside that
                // domain (or NaN/infinity). All other numeric BSON types fit in
                // decimal. Do not convert the other operand to double: rounding
                // decimal.MaxValue upward would incorrectly make it equal to a
                // double whose conversion to decimal overflows.
                var doubleOnLeft = left.IsDouble;
                var value = doubleOnLeft ? left.AsDouble : right.AsDouble;
                var order = double.IsNaN(value) || value < 0 ? -1 : 1;
                return doubleOnLeft ? order : -order;
            }
        }
    }
}
