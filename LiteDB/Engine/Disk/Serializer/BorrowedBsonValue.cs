using System;

namespace LiteDB.Engine
{
    /// <summary>
    /// A scalar BSON value decoded for immediate query evaluation. It never owns
    /// document, array, binary, object-id, or vector payloads.
    /// </summary>
    internal readonly struct BorrowedBsonValue
    {
        private readonly long _integer;
        private readonly double _double;
        private readonly decimal _decimal;
        private readonly DateTime _dateTime;
        private readonly Guid _guid;
        private readonly string _string;

        private BorrowedBsonValue(BsonType type, long integer = 0, double number = 0,
            decimal decimalNumber = 0, DateTime dateTime = default, Guid guid = default,
            string text = null, bool decoded = true)
        {
            this.Type = type;
            this.IsDecoded = decoded;
            _integer = integer;
            _double = number;
            _decimal = decimalNumber;
            _dateTime = dateTime;
            _guid = guid;
            _string = text;
        }

        public BsonType Type { get; }

        /// <summary>
        /// False when only the BSON type was retained. Such values can still be
        /// compared to values of a different type without reading their payload.
        /// </summary>
        public bool IsDecoded { get; }

        public bool IsBoolean => this.Type == BsonType.Boolean;

        public bool AsBoolean => _integer != 0;

        internal string StringValue => _string;

        public static BorrowedBsonValue Null => new BorrowedBsonValue(BsonType.Null);

        public static BorrowedBsonValue FromInt32(int value) =>
            new BorrowedBsonValue(BsonType.Int32, integer: value);

        public static BorrowedBsonValue FromInt64(long value) =>
            new BorrowedBsonValue(BsonType.Int64, integer: value);

        public static BorrowedBsonValue FromDouble(double value) =>
            new BorrowedBsonValue(BsonType.Double, number: value);

        public static BorrowedBsonValue FromDecimal(decimal value) =>
            new BorrowedBsonValue(BsonType.Decimal, decimalNumber: value);

        public static BorrowedBsonValue FromString(string value) => value == null
            ? Null
            : new BorrowedBsonValue(BsonType.String, text: value);

        public static BorrowedBsonValue FromBoolean(bool value) =>
            new BorrowedBsonValue(BsonType.Boolean, integer: value ? 1 : 0);

        public static BorrowedBsonValue FromDateTime(DateTime value) =>
            new BorrowedBsonValue(BsonType.DateTime, dateTime: value.Truncate());

        public static BorrowedBsonValue FromGuid(Guid value) =>
            new BorrowedBsonValue(BsonType.Guid, guid: value);

        public static BorrowedBsonValue FromType(BsonType type) =>
            new BorrowedBsonValue(type, decoded: type == BsonType.Null ||
                type == BsonType.MinValue || type == BsonType.MaxValue);

        internal void WriteTo(ref BorrowedBsonSlot slot)
        {
            slot = default;
            slot.Type = this.Type;
            slot.IsDecoded = this.IsDecoded ? (byte)1 : (byte)0;

            switch (this.Type)
            {
                case BsonType.Int32:
                case BsonType.Int64:
                case BsonType.Boolean: slot.Integer = _integer; break;
                case BsonType.Double: slot.Double = _double; break;
                case BsonType.Decimal: slot.Decimal = _decimal; break;
                case BsonType.DateTime: slot.DateTime = _dateTime; break;
                case BsonType.Guid: slot.Guid = _guid; break;
            }
        }

        internal static BorrowedBsonValue FromSlot(BorrowedBsonSlot slot, string text)
        {
            switch (slot.Type)
            {
                case BsonType.Int32: return FromInt32((int)slot.Integer);
                case BsonType.Int64: return FromInt64(slot.Integer);
                case BsonType.Double: return FromDouble(slot.Double);
                case BsonType.Decimal: return FromDecimal(slot.Decimal);
                case BsonType.String: return FromString(text);
                case BsonType.Boolean: return FromBoolean(slot.Integer != 0);
                case BsonType.DateTime: return new BorrowedBsonValue(BsonType.DateTime,
                    dateTime: slot.DateTime);
                case BsonType.Guid: return FromGuid(slot.Guid);
                default: return new BorrowedBsonValue(slot.Type, decoded: slot.IsDecoded != 0);
            }
        }

        public static bool TryFromOwned(BsonValue value, out BorrowedBsonValue result)
        {
            switch (value.Type)
            {
                case BsonType.Null:
                case BsonType.MinValue:
                case BsonType.MaxValue:
                    result = FromType(value.Type);
                    return true;
                case BsonType.Int32:
                    result = FromInt32(value.AsInt32);
                    return true;
                case BsonType.Int64:
                    result = FromInt64(value.AsInt64);
                    return true;
                case BsonType.Double:
                    result = FromDouble(value.AsDouble);
                    return true;
                case BsonType.Decimal:
                    result = FromDecimal(value.AsDecimal);
                    return true;
                case BsonType.String:
                    result = FromString(value.AsString);
                    return true;
                case BsonType.Guid:
                    result = FromGuid(value.AsGuid);
                    return true;
                case BsonType.Boolean:
                    result = FromBoolean(value.AsBoolean);
                    return true;
                case BsonType.DateTime:
                    result = FromDateTime(value.AsDateTime);
                    return true;
                default:
                    result = FromType(value.Type);
                    return false;
            }
        }

        public bool TryCompare(BorrowedBsonValue other, Collation collation, out int result)
        {
            if (this.Type != other.Type)
            {
                if (this.IsNumber() && other.IsNumber())
                {
                    result = CompareNumbers(this, other);
                    return true;
                }

                result = Math.Sign(this.Type.CompareTo(other.Type));
                return true;
            }

            if (!this.IsDecoded || !other.IsDecoded)
            {
                result = 0;
                return false;
            }

            switch (this.Type)
            {
                case BsonType.Null:
                case BsonType.MinValue:
                case BsonType.MaxValue:
                    result = 0;
                    return true;
                case BsonType.Int32:
                    result = ((int)_integer).CompareTo((int)other._integer);
                    return true;
                case BsonType.Int64:
                    result = _integer.CompareTo(other._integer);
                    return true;
                case BsonType.Double:
                    result = _double.CompareTo(other._double);
                    return true;
                case BsonType.Decimal:
                    result = _decimal.CompareTo(other._decimal);
                    return true;
                case BsonType.String:
                    result = collation.Compare(_string, other._string);
                    return true;
                case BsonType.Guid:
                    result = _guid.CompareTo(other._guid);
                    return true;
                case BsonType.Boolean:
                    result = (_integer != 0).CompareTo(other._integer != 0);
                    return true;
                case BsonType.DateTime:
                    var left = _dateTime.Kind == DateTimeKind.Utc ? _dateTime : _dateTime.ToUniversalTime();
                    var right = other._dateTime.Kind == DateTimeKind.Utc ? other._dateTime : other._dateTime.ToUniversalTime();
                    result = left.CompareTo(right);
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        public bool TryMaterialize(out BsonValue value)
        {
            if (!this.IsDecoded)
            {
                value = null;
                return false;
            }

            switch (this.Type)
            {
                case BsonType.Null: value = BsonValue.Null; return true;
                case BsonType.MinValue: value = BsonValue.MinValue; return true;
                case BsonType.MaxValue: value = BsonValue.MaxValue; return true;
                case BsonType.Int32: value = new BsonValue((int)_integer); return true;
                case BsonType.Int64: value = new BsonValue(_integer); return true;
                case BsonType.Double: value = new BsonValue(_double); return true;
                case BsonType.Decimal: value = new BsonValue(_decimal); return true;
                case BsonType.String: value = new BsonValue(_string); return true;
                case BsonType.Guid: value = new BsonValue(_guid); return true;
                case BsonType.Boolean: value = new BsonValue(_integer != 0); return true;
                case BsonType.DateTime: value = new BsonValue(_dateTime); return true;
                default:
                    value = null;
                    return false;
            }
        }

        private bool IsNumber() => this.Type == BsonType.Int32 || this.Type == BsonType.Int64 ||
            this.Type == BsonType.Double || this.Type == BsonType.Decimal;

        private decimal AsDecimal()
        {
            switch (this.Type)
            {
                case BsonType.Int32:
                case BsonType.Int64: return _integer;
                case BsonType.Decimal: return _decimal;
                default: throw new NotSupportedException($"{this.Type} has no exact decimal value.");
            }
        }

        private static int CompareNumbers(BorrowedBsonValue left, BorrowedBsonValue right)
        {
            if (left.Type == BsonType.Double)
            {
                if (right.Type == BsonType.Double) return left._double.CompareTo(right._double);

                return right.Type == BsonType.Decimal
                    ? -BsonNumericComparer.CompareDecimalToDouble(right._decimal, left._double)
                    : -BsonNumericComparer.CompareInt64ToDouble(right._integer, left._double);
            }

            if (right.Type == BsonType.Double)
            {
                return left.Type == BsonType.Decimal
                    ? BsonNumericComparer.CompareDecimalToDouble(left._decimal, right._double)
                    : BsonNumericComparer.CompareInt64ToDouble(left._integer, right._double);
            }

            if (left.Type == BsonType.Decimal || right.Type == BsonType.Decimal)
            {
                return left.AsDecimal().CompareTo(right.AsDecimal());
            }

            return left._integer.CompareTo(right._integer);
        }
    }
}
