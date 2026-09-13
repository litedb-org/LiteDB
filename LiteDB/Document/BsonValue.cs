using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Represent a Bson Value used in BsonDocument
    /// </summary>
    public class BsonValue : IComparable<BsonValue>, IEquatable<BsonValue>
    {
        public static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Represent a Null bson type
        /// </summary>
        public static BsonValue Null = new BsonValue(BsonType.Null, null);

        /// <summary>
        /// Represent a MinValue bson type
        /// </summary>
        public static BsonValue MinValue = new BsonValue(BsonType.MinValue, "-oo");

        /// <summary>
        /// Represent a MaxValue bson type
        /// </summary>
        public static BsonValue MaxValue = new BsonValue(BsonType.MaxValue, "+oo");

        /// <summary>
        /// Create a new document used in DbRef => { $id: id, $ref: collection }
        /// </summary>
        public static BsonDocument DbRef(BsonValue id, string collection) => new BsonDocument { ["$id"] = id, ["$ref"] = collection };

        /// <summary>
        /// Indicate BsonType of this BsonValue
        /// </summary>
        public BsonType Type { get; }

        /// <summary>
        /// Get internal .NET value object
        /// </summary>
        public virtual object RawValue { get; }

        #region Constructor

        public BsonValue()
        {
            this.Type = BsonType.Null;
            this.RawValue = null;
        }

        public BsonValue(Int32 value)
        {
            this.Type = BsonType.Int32;
            this.RawValue = value;
        }

        public BsonValue(Int64 value)
        {
            this.Type = BsonType.Int64;
            this.RawValue = value;
        }

        public BsonValue(Double value)
        {
            this.Type = BsonType.Double;
            this.RawValue = value;
        }

        public BsonValue(Decimal value)
        {
            this.Type = BsonType.Decimal;
            this.RawValue = value;
        }

        public BsonValue(String value)
        {
            this.Type = value == null ? BsonType.Null : BsonType.String;
            this.RawValue = value;
        }

        public BsonValue(Byte[] value)
        {
            this.Type = value == null ? BsonType.Null : BsonType.Binary;
            this.RawValue = value;
        }

        public BsonValue(ObjectId value)
        {
            this.Type = value == null ? BsonType.Null : BsonType.ObjectId;
            this.RawValue = value;
        }

        public BsonValue(Guid value)
        {
            this.Type = BsonType.Guid;
            this.RawValue = value;
        }

        public BsonValue(Boolean value)
        {
            this.Type = BsonType.Boolean;
            this.RawValue = value;
        }

        public BsonValue(DateTime value)
        {
            this.Type = BsonType.DateTime;
            this.RawValue = value.Truncate();
        }

        protected BsonValue(BsonType type, object rawValue)
        {
            this.Type = type;
            this.RawValue = rawValue;
        }

        public BsonValue(object value)
        {
            this.RawValue = value;

            if (value == null) this.Type = BsonType.Null;
            else if (value is Int32) this.Type = BsonType.Int32;
            else if (value is Int64) this.Type = BsonType.Int64;
            else if (value is Double) this.Type = BsonType.Double;
            else if (value is Decimal) this.Type = BsonType.Decimal;
            else if (value is String) this.Type = BsonType.String;
            else if (value is IDictionary<string, BsonValue>) this.Type = BsonType.Document;
            else if (value is IList<BsonValue>) this.Type = BsonType.Array;
            else if (value is Byte[]) this.Type = BsonType.Binary;
            else if (value is ObjectId) this.Type = BsonType.ObjectId;
            else if (value is Guid) this.Type = BsonType.Guid;
            else if (value is Boolean) this.Type = BsonType.Boolean;
            else if (value is DateTime)
            {
                this.Type = BsonType.DateTime;
                this.RawValue = ((DateTime)value).Truncate();
            }
            else if (value is BsonValue)
            {
                var v = (BsonValue)value;
                this.Type = v.Type;
                this.RawValue = v.RawValue;
            }
            else
            {
                // test for array or dictionary (document)
                var enumerable = value as System.Collections.IEnumerable;
                var dictionary = value as System.Collections.IDictionary;

                // test first for dictionary (because IDictionary implements IEnumerable)
                if (dictionary != null)
                {
                    var dict = new Dictionary<string, BsonValue>();

                    foreach (var key in dictionary.Keys)
                    {
                        dict.Add(key.ToString(), new BsonValue(dictionary[key]));
                    }

                    this.Type = BsonType.Document;
                    this.RawValue = dict;
                }
                else if (enumerable != null)
                {
                    var list = new List<BsonValue>();

                    foreach (var x in enumerable)
                    {
                        list.Add(new BsonValue(x));
                    }

                    this.Type = BsonType.Array;
                    this.RawValue = list;
                }
                else
                {
                    throw new InvalidCastException("Value is not a valid BSON data type - Use Mapper.ToDocument for more complex types converts");
                }
            }
        }

        #endregion

        #region Index "this" property

        /// <summary>
        /// Get/Set a field for document. Fields are case sensitive - Works only when value are document
        /// </summary>
        public virtual BsonValue this[string name]
        {
            get => throw new InvalidOperationException("Cannot access non-document type value on " + this.RawValue);
            set => throw new InvalidOperationException("Cannot access non-document type value on " + this.RawValue);
        }

        /// <summary>
        /// Get/Set value in array position. Works only when value are array
        /// </summary>
        public virtual BsonValue this[int index]
        {
            get => throw new InvalidOperationException("Cannot access non-array type value on " + this.RawValue);
            set => throw new InvalidOperationException("Cannot access non-array type value on " + this.RawValue);
        }

        #endregion

        #region Convert types

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public BsonArray AsArray => this as BsonArray;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public BsonDocument AsDocument => this as BsonDocument;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public Byte[] AsBinary => this.RawValue as Byte[];

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool AsBoolean => (bool)this.RawValue;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public string AsString => (string)this.RawValue;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public int AsInt32 => Convert.ToInt32(this.RawValue);

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public long AsInt64 => Convert.ToInt64(this.RawValue);

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public double AsDouble => Convert.ToDouble(this.RawValue);

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public decimal AsDecimal => Convert.ToDecimal(this.RawValue);

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public DateTime AsDateTime => (DateTime)this.RawValue;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public ObjectId AsObjectId => (ObjectId)this.RawValue;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public Guid AsGuid => (Guid)this.RawValue;

        #endregion

        #region IsTypes

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsNull => this.Type == BsonType.Null;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsArray => this.Type == BsonType.Array;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsDocument => this.Type == BsonType.Document;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsInt32 => this.Type == BsonType.Int32;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsInt64 => this.Type == BsonType.Int64;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsDouble => this.Type == BsonType.Double;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsDecimal => this.Type == BsonType.Decimal;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsNumber => this.IsInt32 || this.IsInt64 || this.IsDouble || this.IsDecimal;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsBinary => this.Type == BsonType.Binary;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsBoolean => this.Type == BsonType.Boolean;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsString => this.Type == BsonType.String;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsObjectId => this.Type == BsonType.ObjectId;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsGuid => this.Type == BsonType.Guid;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsDateTime => this.Type == BsonType.DateTime;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsMinValue => this.Type == BsonType.MinValue;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public bool IsMaxValue => this.Type == BsonType.MaxValue;

        #endregion

        #region Implicit Ctor

        // Int32
        public static implicit operator Int32(BsonValue value)
        {
            return (Int32)value.RawValue;
        }

        // Int32
        public static implicit operator BsonValue(Int32 value)
        {
            return new BsonValue(value);
        }

        // Int64
        public static implicit operator Int64(BsonValue value)
        {
            return (Int64)value.RawValue;
        }

        // Int64
        public static implicit operator BsonValue(Int64 value)
        {
            return new BsonValue(value);
        }

        // Double
        public static implicit operator Double(BsonValue value)
        {
            return (Double)value.RawValue;
        }

        // Double
        public static implicit operator BsonValue(Double value)
        {
            return new BsonValue(value);
        }

        // Decimal
        public static implicit operator Decimal(BsonValue value)
        {
            return (Decimal)value.RawValue;
        }

        // Decimal
        public static implicit operator BsonValue(Decimal value)
        {
            return new BsonValue(value);
        }

        // UInt64 (to avoid ambigous between Double-Decimal)
        public static implicit operator UInt64(BsonValue value)
        {
            return (UInt64)value.RawValue;
        }

        // Decimal
        public static implicit operator BsonValue(UInt64 value)
        {
            return new BsonValue((Double)value);
        }

        // String
        public static implicit operator String(BsonValue value)
        {
            return (String)value.RawValue;
        }

        // String
        public static implicit operator BsonValue(String value)
        {
            return new BsonValue(value);
        }

        // Binary
        public static implicit operator Byte[] (BsonValue value)
        {
            return (Byte[])value.RawValue;
        }

        // Binary
        public static implicit operator BsonValue(Byte[] value)
        {
            return new BsonValue(value);
        }

        // ObjectId
        public static implicit operator ObjectId(BsonValue value)
        {
            return (ObjectId)value.RawValue;
        }

        // ObjectId
        public static implicit operator BsonValue(ObjectId value)
        {
            return new BsonValue(value);
        }

        // Guid
        public static implicit operator Guid(BsonValue value)
        {
            return (Guid)value.RawValue;
        }

        // Guid
        public static implicit operator BsonValue(Guid value)
        {
            return new BsonValue(value);
        }

        // Boolean
        public static implicit operator Boolean(BsonValue value)
        {
            return (Boolean)value.RawValue;
        }

        // Boolean
        public static implicit operator BsonValue(Boolean value)
        {
            return new BsonValue(value);
        }

        // DateTime
        public static implicit operator DateTime(BsonValue value)
        {
            return (DateTime)value.RawValue;
        }

        // DateTime
        public static implicit operator BsonValue(DateTime value)
        {
            return new BsonValue(value);
        }

        // +
        public static BsonValue operator +(BsonValue left, BsonValue right)
        {
            if (!left.IsNumber || !right.IsNumber) return BsonValue.Null;

            if (left.IsInt32 && right.IsInt32) return left.AsInt32 + right.AsInt32;
            if (left.IsInt64 && right.IsInt64) return left.AsInt64 + right.AsInt64;
            if (left.IsDouble && right.IsDouble) return left.AsDouble + right.AsDouble;
            if (left.IsDecimal && right.IsDecimal) return left.AsDecimal + right.AsDecimal;

            var result = left.AsDecimal + right.AsDecimal;
            var type = (BsonType)Math.Max((int)left.Type, (int)right.Type);

            return
                type == BsonType.Int64 ? new BsonValue((Int64)result) :
                type == BsonType.Double ? new BsonValue((Double)result) :
                new BsonValue(result);
        }

        // -
        public static BsonValue operator -(BsonValue left, BsonValue right)
        {
            if (!left.IsNumber || !right.IsNumber) return BsonValue.Null;

            if (left.IsInt32 && right.IsInt32) return left.AsInt32 - right.AsInt32;
            if (left.IsInt64 && right.IsInt64) return left.AsInt64 - right.AsInt64;
            if (left.IsDouble && right.IsDouble) return left.AsDouble - right.AsDouble;
            if (left.IsDecimal && right.IsDecimal) return left.AsDecimal - right.AsDecimal;

            var result = left.AsDecimal - right.AsDecimal;
            var type = (BsonType)Math.Max((int)left.Type, (int)right.Type);

            return
                type == BsonType.Int64 ? new BsonValue((Int64)result) :
                type == BsonType.Double ? new BsonValue((Double)result) :
                new BsonValue(result);
        }

        // *
        public static BsonValue operator *(BsonValue left, BsonValue right)
        {
            if (!left.IsNumber || !right.IsNumber) return BsonValue.Null;

            if (left.IsInt32 && right.IsInt32) return left.AsInt32 * right.AsInt32;
            if (left.IsInt64 && right.IsInt64) return left.AsInt64 * right.AsInt64;
            if (left.IsDouble && right.IsDouble) return left.AsDouble * right.AsDouble;
            if (left.IsDecimal && right.IsDecimal) return left.AsDecimal * right.AsDecimal;

            var result = left.AsDecimal * right.AsDecimal;
            var type = (BsonType)Math.Max((int)left.Type, (int)right.Type);

            return
                type == BsonType.Int64 ? new BsonValue((Int64)result) :
                type == BsonType.Double ? new BsonValue((Double)result) :
                new BsonValue(result);
        }

        // /
        public static BsonValue operator /(BsonValue left, BsonValue right)
        {
            if (!left.IsNumber || !right.IsNumber) return BsonValue.Null;
            if (left.IsDecimal || right.IsDecimal) return left.AsDecimal / right.AsDecimal;

            return left.AsDouble / right.AsDouble;
        }

        // %
        public static BsonValue operator %(BsonValue left, BsonValue right)
        {
            if (!left.IsNumber || !right.IsNumber) return BsonValue.Null;
            if (left.IsInt32 && right.IsInt32) return left.AsInt32 % right.AsInt32;
            if (left.IsInt64 && right.IsInt64) return left.AsInt64 % right.AsInt64;
            if (left.IsDecimal && right.IsDecimal) return left.AsDecimal % right.AsDecimal;

            return left.AsDouble % right.AsDouble;
        }

        public override string ToString()
        {
            return JsonSerializer.Serialize(this);
        }

        #endregion

        #region IComparable<BsonValue>, IEquatable<BsonValue>

        public virtual int CompareTo(BsonValue other)
        {
            return this.CompareTo(other, Collation.Binary);
        }

        public virtual int CompareTo(BsonValue other, Collation collation)
        {
            // first, test if types are different
            if (this.Type != other.Type)
            {
                // if both values are number, convert them to Decimal (128 bits) to compare
                // it's the slowest way, but more secure
                if (this.IsNumber && other.IsNumber)
                {
                    return Convert.ToDecimal(this.RawValue).CompareTo(Convert.ToDecimal(other.RawValue));
                }
                // if not, order by sort type order
                else
                {
                    var result = this.Type.CompareTo(other.Type);
                    return result < 0 ? -1 : result > 0 ? +1 : 0;
                }
            }

            // for both values with same data type just compare
            switch (this.Type)
            {
                case BsonType.Null:
                case BsonType.MinValue:
                case BsonType.MaxValue:
                    return 0;

                case BsonType.Int32: return this.AsInt32.CompareTo(other.AsInt32);
                case BsonType.Int64: return this.AsInt64.CompareTo(other.AsInt64);
                case BsonType.Double: return this.AsDouble.CompareTo(other.AsDouble);
                case BsonType.Decimal: return this.AsDecimal.CompareTo(other.AsDecimal);

                case BsonType.String: return collation.Compare(this.AsString, other.AsString);

                case BsonType.Document: return this.AsDocument.CompareTo(other);
                case BsonType.Array: return this.AsArray.CompareTo(other);

                case BsonType.Binary: return this.AsBinary.BinaryCompareTo(other.AsBinary);
                case BsonType.ObjectId: return this.AsObjectId.CompareTo(other.AsObjectId);
                case BsonType.Guid: return this.AsGuid.CompareTo(other.AsGuid);

                case BsonType.Boolean: return this.AsBoolean.CompareTo(other.AsBoolean);
                case BsonType.DateTime:
                    var d0 = this.AsDateTime;
                    var d1 = other.AsDateTime;
                    if (d0.Kind != DateTimeKind.Utc) d0 = d0.ToUniversalTime();
                    if (d1.Kind != DateTimeKind.Utc) d1 = d1.ToUniversalTime();
                    return d0.CompareTo(d1);

                default: throw new NotImplementedException();
            }
        }

        public bool Equals(BsonValue other)
        {
            return this.CompareTo(other) == 0;
        }

        #endregion

        #region Operators

        public static bool operator ==(BsonValue lhs, BsonValue rhs)
        {
            if (object.ReferenceEquals(lhs, null)) return object.ReferenceEquals(rhs, null);
            if (object.ReferenceEquals(rhs, null)) return false; // don't check type because sometimes different types can be ==

            return lhs.Equals(rhs);
        }

        public static bool operator !=(BsonValue lhs, BsonValue rhs)
        {
            return !(lhs == rhs);
        }

        public static bool operator >=(BsonValue lhs, BsonValue rhs)
        {
            return lhs.CompareTo(rhs) >= 0;
        }

        public static bool operator >(BsonValue lhs, BsonValue rhs)
        {
            return lhs.CompareTo(rhs) > 0;
        }

        public static bool operator <(BsonValue lhs, BsonValue rhs)
        {
            return lhs.CompareTo(rhs) < 0;
        }

        public static bool operator <=(BsonValue lhs, BsonValue rhs)
        {
            return lhs.CompareTo(rhs) <= 0;
        }

        public override bool Equals(object obj)
        {
            if (obj is BsonValue other)
            {
                return this.Equals(other);
            }

            return false;
        }

        public override int GetHashCode()
        {
            var hash = 17;
            hash = 37 * hash + this.Type.GetHashCode();
            hash = 37 * hash + (this.RawValue?.GetHashCode() ?? 0);
            return hash;
        }

        #endregion

        #region GetBytesCount()

        /// <summary>
        /// Returns how many bytes this BsonValue will consume when converted into binary BSON
        /// If recalc = false, use cached length value (from Array/Document only)
        /// </summary>
        internal virtual int GetBytesCount(bool recalc)
        {
            switch (this.Type)
            {
                case BsonType.Null:
                case BsonType.MinValue:
                case BsonType.MaxValue: return 0;

                case BsonType.Int32: return 4;
                case BsonType.Int64: return 8;
                case BsonType.Double: return 8;
                case BsonType.Decimal: return 16;

                case BsonType.String: return StringEncoding.UTF8.GetByteCount(this.AsString);

                case BsonType.Binary: return this.AsBinary.Length;
                case BsonType.ObjectId: return 12;
                case BsonType.Guid: return 16;

                case BsonType.Boolean: return 1;
                case BsonType.DateTime: return 8;

                case BsonType.Document: return this.AsDocument.GetBytesCount(recalc);
                case BsonType.Array: return this.AsArray.GetBytesCount(recalc);
            }

            throw new ArgumentException();
        }

        /// <summary>
        /// Get how many bytes one single element will used in BSON format
        /// </summary>
        protected int GetBytesCountElement(string key, BsonValue value)
        {
            // check if data type is variant
            var variant = value.Type == BsonType.String || value.Type == BsonType.Binary || value.Type == BsonType.Guid;

            return
                1 + // element type
                StringEncoding.UTF8.GetByteCount(key) + // CString
                1 + // CString \0
                value.GetBytesCount(true) +
                (variant ? 5 : 0); // bytes.Length + 0x??
        }

        #endregion

    }
}
