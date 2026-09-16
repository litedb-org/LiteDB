using System;
using System.Diagnostics;

namespace LiteDB
{
    public partial class BsonValue
    {
        #region Index "this" property

        /// <summary>
        /// Get/Set a field for document. Fields are case sensitive - Works only when value are document
        /// </summary>
        public virtual BsonValue this[string name]
        {
            get
            {
                if (this.IsDocument) return this.AsDocument[name];

                throw new InvalidOperationException("Cannot access non-document type value on " + this.RawValue);
            }
            set
            {
                if (this.IsDocument)
                {
                    this.AsDocument[name] = value;
                    return;
                }

                throw new InvalidOperationException("Cannot access non-document type value on " + this.RawValue);
            }
        }

        /// <summary>
        /// Get/Set value in array position. Works only when value are array
        /// </summary>
        public virtual BsonValue this[int index]
        {
            get
            {
                if (this.IsArray) return this.AsArray[index];

                throw new InvalidOperationException("Cannot access non-array type value on " + this.RawValue);
            }
            set
            {
                if (this.IsArray)
                {
                    this.AsArray[index] = value;
                    return;
                }

                throw new InvalidOperationException("Cannot access non-array type value on " + this.RawValue);
            }
        }

        #endregion

        #region Convert collection types

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public BsonArray AsArray => this is BsonArray array
            ? array
            : this.IsArray ? this.RawValue as BsonArray : null;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public BsonDocument AsDocument => this is BsonDocument document
            ? document
            : this.IsDocument ? this.RawValue as BsonDocument : null;

        #endregion

        #region Collection hash codes

        public override int GetHashCode()
        {
            return GetEqualityHashCode(this);
        }

        private static int GetEqualityHashCode(BsonValue value)
        {
            if (value.IsNumber)
            {
                if (!IsDecimalConvertible(value))
                {
                    return CombineHashCodes(value.Type.GetHashCode(), value.AsDouble.GetHashCode());
                }

                return Convert.ToDecimal(value.RawValue).GetHashCode();
            }

            switch (value.Type)
            {
                case BsonType.Array: return GetArrayHashCode(value.AsArray);
                case BsonType.Document: return GetDocumentHashCode(value.AsDocument);
                case BsonType.Binary: return GetSequenceHashCode(value.Type, value.AsBinary);
                case BsonType.Vector: return GetSequenceHashCode(value.Type, value.AsVector);
                case BsonType.DateTime:
                    var date = value.AsDateTime;
                    if (date.Kind != DateTimeKind.Utc) date = date.ToUniversalTime();
                    return CombineHashCodes(value.Type.GetHashCode(), date.Ticks.GetHashCode());
                default:
                    return CombineHashCodes(value.Type.GetHashCode(), value.RawValue?.GetHashCode() ?? 0);
            }
        }

        private static int GetArrayHashCode(BsonArray array)
        {
            var hash = CombineHashCodes(BsonType.Array.GetHashCode(), array.Count);

            foreach (var value in array)
            {
                hash = CombineHashCodes(hash, GetEqualityHashCode(value));
            }

            return hash;
        }

        private static int GetDocumentHashCode(BsonDocument document)
        {
            var elementsHash = 0;

            foreach (var element in document)
            {
                // BsonDocument.CompareTo reads a missing key as Null, so { a: null } equals
                // { b: null }. Null-valued elements must not contribute their key to the hash.
                if (element.Value.IsNull) continue;

                var elementHash = CombineHashCodes(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(element.Key),
                    GetEqualityHashCode(element.Value));
                elementsHash = unchecked(elementsHash + elementHash);
            }

            return CombineHashCodes(
                CombineHashCodes(BsonType.Document.GetHashCode(), document.Count),
                elementsHash);
        }

        private static int GetSequenceHashCode<T>(BsonType type, T[] values)
        {
            var hash = CombineHashCodes(type.GetHashCode(), values.Length);

            foreach (var value in values)
            {
                hash = CombineHashCodes(hash, value.GetHashCode());
            }

            return hash;
        }

        private static int CombineHashCodes(int left, int right)
        {
            return unchecked(37 * (37 * 17 + left) + right);
        }

        #endregion

        #region Numeric range helpers

        /// <summary>
        /// True when Convert.ToDecimal can hold this number. Only doubles can fail: NaN, infinity,
        /// or a magnitude of 2^96 and above. (double)Decimal.MaxValue rounds up to exactly 2^96,
        /// which itself overflows, so the bounds are exclusive.
        /// </summary>
        internal static bool IsDecimalConvertible(BsonValue value)
        {
            if (!value.IsDouble) return true;

            var number = value.AsDouble;

            return !Double.IsNaN(number) && !Double.IsInfinity(number) &&
                number < (double)Decimal.MaxValue && number > (double)Decimal.MinValue;
        }

        /// <summary>
        /// Sort position of a double that decimal cannot hold, relative to any decimal-convertible
        /// number. NaN and negative values sort first, matching Double.CompareTo.
        /// </summary>
        private static int OutOfDecimalRangeSign(double number)
        {
            return number > 0 ? 1 : -1;
        }

        #endregion
    }
}
