using System;
using System.Collections.Generic;

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
                // define skip length according type
                var length =
                    (type == 0x0A || type == 0xFF || type == 0x7F) ? 0 : // Null, MinValue, MaxValue
                    (type == 0x08) ? 1 : // Boolean
                    (type == 0x10) ? 4 : // Int
                    (type == 0x01 || type == 0x12 || type == 0x09) ? 8 : // Double, Int64, DateTime
                    (type == 0x07) ? 12 : // ObjectId
                    (type == 0x13) ? 16 : // Decimal
                    (type == 0x02) ? reader.ReadInt32() : // String
                    (type == 0x05) ? reader.ReadInt32() + 1 : // Binary (+1 for subtype)
                    (type == 0x03 || type == 0x04) ? reader.ReadInt32() - 4 : 0; // Document, Array (-4 to Length + zero)

                if (length > 0)
                {
                    reader.Skip(length);
                }

                return null;
            }

            if (type == 0x01) // Double
            {
                return reader.ReadDouble();
            }
            else if (type == 0x02) // String
            {
                var length = reader.ReadInt32();
                var value = reader.ReadString(length - 1);
                reader.Skip(1); // read '\0'
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
                var subType = reader.ReadByte();
                var bytes = reader.ReadBytes(length);

                switch (subType)
                {
                    case 0x00: return bytes;
                    case 0x04: return new Guid(bytes);
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

                // catch specific values for MaxValue / MinValue #19
                if (ts == 253402300800000) return DateTime.MaxValue;
                if (ts == -62135596800000) return DateTime.MinValue;

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

    }
}
