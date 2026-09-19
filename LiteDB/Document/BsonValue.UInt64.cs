using System;

namespace LiteDB
{
    public partial class BsonValue
    {
        private readonly bool _isUInt64;

        private BsonValue(UInt64 value)
        {
            _isUInt64 = true;
            this.Type = BsonType.Int64;
            this.RawValue = unchecked((Int64)value);
        }

        internal ulong AsUInt64
        {
            get
            {
                if (this.IsInt64) return unchecked((UInt64)this.AsInt64);
                if (this.IsInt32) return unchecked((UInt64)this.AsInt32);

                throw new InvalidCastException();
            }
        }

        internal ulong AsUInt64OrLegacy
        {
            get
            {
                if (!this.IsDouble) return this.AsUInt64;

                var value = this.AsDouble;

                if (Double.IsNaN(value) || value < 0d || value > 18446744073709551616d)
                {
                    throw new OverflowException();
                }

                return value == 18446744073709551616d
                    ? UInt64.MaxValue
                    : Convert.ToUInt64(value);
            }
        }

        internal bool TryGetLegacyUInt64(out BsonValue legacy)
        {
            legacy = null;

            if (!_isUInt64) return false;

            legacy = new BsonValue((Double)this.AsUInt64);

            // Values exactly representable as Double already compare equal under
            // the normal cross-number comparison and need no compatibility probe.
            return this.CompareTo(legacy) != 0;
        }

        internal bool IsLegacyUInt64Match(BsonValue value, Collation collation)
        {
            if (!_isUInt64 || !value.IsDouble) return false;

            var legacy = new BsonValue((Double)this.AsUInt64);

            return collation.Equals(legacy, value);
        }

        internal static bool UInt64Equals(BsonValue left, BsonValue right, Collation collation)
        {
            return collation.Equals(left, right) ||
                left.IsLegacyUInt64Match(right, collation) ||
                right.IsLegacyUInt64Match(left, collation);
        }

        // UInt64 is stored as Int64 now, but legacy direct writes used Double.
        // Keep both readable so old files do not need a rewrite just to load values.
        public static implicit operator UInt64(BsonValue value)
        {
            return value.AsUInt64;
        }

        // UInt64 uses the same signed Int64 bits as BsonMapper.
        public static implicit operator BsonValue(UInt64 value)
        {
            return new BsonValue(value);
        }
    }
}
