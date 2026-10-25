using System;

namespace LiteDB.Engine
{
    internal partial class BufferReader
    {
        /// <summary>
        /// Read a single IndexKey, including the extended string/binary length encoded in its type byte.
        /// </summary>
        public BsonValue ReadIndexKey()
        {
            var typeByte = this.ReadByte();
            ExtendedLengthHelper.ReadLength(typeByte, 0, out var type, out _);
            if (type != BsonType.String && type != BsonType.Binary)
            {
                type = (BsonType)typeByte;
            }

            switch (type)
            {
                case BsonType.Null: return BsonValue.Null;

                case BsonType.Int32: return this.ReadInt32();
                case BsonType.Int64: return this.ReadInt64();
                case BsonType.Double: return this.ReadDouble();
                case BsonType.Decimal: return this.ReadDecimal();

                case BsonType.String:
                    ExtendedLengthHelper.ReadLength(typeByte, this.ReadByte(), out _, out var stringLength);
                    return this.ReadString(stringLength);

                case BsonType.Document: return this.ReadDocument(null).GetValue();
                case BsonType.Array: return this.ReadArray().GetValue();

                case BsonType.Binary:
                    ExtendedLengthHelper.ReadLength(typeByte, this.ReadByte(), out _, out var binaryLength);
                    return this.ReadBytes(binaryLength);
                case BsonType.ObjectId: return this.ReadObjectId();
                case BsonType.Guid: return this.ReadGuid();

                case BsonType.Boolean: return this.ReadBoolean();
                case BsonType.DateTime: return this.ReadDateTime();

                case BsonType.MinValue: return BsonValue.MinValue;
                case BsonType.MaxValue: return BsonValue.MaxValue;

                case BsonType.Vector: return this.ReadVector();

                default: throw new NotImplementedException();
            }
        }
    }
}
