using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal static class BsonElementReader
    {
        /// <summary>
        /// Reads an element (key-value) from an reader
        /// </summary>
        internal static BsonValue Read(BufferReader reader, HashSet<string> remaining, bool utcDate, out string name)
        {
            var type = reader.ReadByte();
            name = reader.ReadCString();

            // check if need skip this element
            if (remaining != null && !remaining.Contains(name))
            {
                SkipValue(reader, type);
                return null;
            }

            if (type == 0x01) // Double
            {
                return reader.ReadDouble();
            }
            else if (type == 0x02) // String
            {
                var length = reader.ReadInt32();
                ENSURE(length >= 1 && length <= MAX_DOCUMENT_SIZE, "string length exceeds the document limit");
                var value = reader.ReadString(length - 1);
                ENSURE(reader.ReadByte() == 0, "string must end with a null terminator");
                return value;
            }
            else if (type == 0x03) // Document
            {
                return reader.ReadDocument().GetValue();
            }
            else if (type == 0x04) // Array
            {
                return reader.ReadArray().GetValue();
            }
            else if (type == 0x05) // Binary
            {
                var length = reader.ReadInt32();
                ENSURE(length >= 0 && length <= MAX_DOCUMENT_SIZE, "binary length exceeds the document limit");
                var subType = reader.ReadByte();
                var bytes = reader.ReadBytes(length);

                switch (subType)
                {
                    case 0x00: return bytes;
                    case 0x04: return new Guid(bytes);
                    default: throw new NotSupportedException("BSON binary subtype not supported");
                }
            }
            else if (type == 0x07) // ObjectId
            {
                return reader.ReadObjectId();
            }
            else if (type == 0x08) // Boolean
            {
                return reader.ReadBoolean();
            }
            else if (type == 0x09) // DateTime
            {
                var ts = reader.ReadInt64();

                // MaxValue / MinValue sentinels #19; a damaged value beyond them clamps so the document stays readable #2930
                if (ts >= 253402300800000) return DateTime.MaxValue;
                if (ts <= -62135596800000) return DateTime.MinValue;

                var date = BsonValue.UnixEpoch.AddMilliseconds(ts);

                return utcDate ? date : date.ToLocalTime();
            }
            else if (type == 0x0A) // Null
            {
                return BsonValue.Null;
            }
            else if (type == 0x10) // Int32
            {
                return reader.ReadInt32();
            }
            else if (type == 0x12) // Int64
            {
                return reader.ReadInt64();
            }
            else if (type == 0x13) // Decimal
            {
                return reader.ReadDecimal();
            }
            else if (type == 0xFF) // MinKey
            {
                return BsonValue.MinValue;
            }
            else if (type == 0x7F) // MaxKey
            {
                return BsonValue.MaxValue;
            }
            else if (type == 0x64) // Vector
            {
                return reader.ReadVector();
            }

            throw new NotSupportedException("BSON type not supported");
        }

        internal static void SkipValue(BufferReader reader, byte type)
        {
            switch (type)
            {
                case 0x01: reader.ReadDouble(); return;
                case 0x02:
                    var stringLength = reader.ReadInt32();
                    ENSURE(stringLength >= 1 && stringLength <= MAX_DOCUMENT_SIZE,
                        "string length exceeds the document limit");
                    reader.Skip(stringLength - 1);
                    ENSURE(reader.ReadByte() == 0, "string must end with a null terminator");
                    return;
                case 0x03: reader.SkipDocument(); return;
                case 0x04: reader.SkipArray(); return;
                case 0x05:
                    var binaryLength = reader.ReadInt32();
                    ENSURE(binaryLength >= 0 && binaryLength <= MAX_DOCUMENT_SIZE,
                        "binary length exceeds the document limit");
                    var subtype = reader.ReadByte();
                    ENSURE(subtype == 0x00 || subtype == 0x04, "binary subtype is not supported");
                    ENSURE(subtype != 0x04 || binaryLength == 16, "GUID binary value must contain 16 bytes");
                    reader.Skip(binaryLength);
                    return;
                case 0x07: reader.ReadObjectId(); return;
                case 0x08: reader.ReadBoolean(); return;
                case 0x09: reader.ReadInt64(); return;
                case 0x0A:
                case 0x7F:
                case 0xFF: return;
                case 0x10: reader.ReadInt32(); return;
                case 0x12: reader.ReadInt64(); return;
                case 0x13: reader.ReadDecimal(); return;
                case 0x64:
                    reader.Skip(checked(reader.ReadUInt16() * 4));
                    return;
                default: throw new NotSupportedException("BSON type not supported");
            }
        }

    }
}
