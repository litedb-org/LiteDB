using System;

namespace LiteDB.Engine
{
    internal static class LegacyIndexComparison
    {
        // Culture-sensitive strings cannot establish damage without the writer's
        // sort tables. Containers also changed their comparison in v6. Keep these
        // ambiguous cases on the collation/migration path, never automatic salvage.
        internal static bool IsInvariantViolation(BsonValue left, BsonValue right, bool unique)
        {
            if (left.IsString && right.IsString)
                return unique && string.Equals(left.AsString, right.AsString, StringComparison.Ordinal);
            if (left.Type == right.Type && (left.IsDocument || left.IsArray)) return false;

            int legacy;
            if (left.Type != right.Type && left.IsNumber && right.IsNumber)
            {
                // Released v5 compared mixed numbers through decimal. Overflow
                // and nonfinite values cannot prove corruption under that comparer.
                try { legacy = Convert.ToDecimal(left.RawValue).CompareTo(Convert.ToDecimal(right.RawValue)); }
                catch (OverflowException) { return false; }
            }
            else if (left.IsObjectId && right.IsObjectId)
            {
                var a = left.AsObjectId;
                var b = right.AsObjectId;
                legacy = a.Timestamp.CompareTo(b.Timestamp);
                if (legacy == 0) legacy = a.Machine.CompareTo(b.Machine);
                if (legacy == 0) legacy = a.Pid.CompareTo(b.Pid);
                if (legacy == 0) legacy = a.Increment.CompareTo(b.Increment);
            }
            else legacy = left.CompareTo(right, Collation.Binary);

            return legacy > 0 || (legacy == 0 && unique);
        }
    }
}
