using LiteDB.Engine;
using System;
using System.Linq;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    internal static class BufferSliceExtensions
    {
        #region Read Extensions

        public static bool ReadBool(this BufferSlice buffer, int offset)
        {
            return buffer.Array[buffer.Offset + offset] != 0;
        }

        public static byte ReadByte(this BufferSlice buffer, int offset)
        {
            return buffer.Array[buffer.Offset + offset];
        }

        public static Int16 ReadInt16(this BufferSlice buffer, int offset)
        {
            return BitConverter.ToInt16(buffer.Array, buffer.Offset + offset);
        }

        public static UInt16 ReadUInt16(this BufferSlice buffer, int offset)
        {
            return BitConverter.ToUInt16(buffer.Array, buffer.Offset + offset);
        }

        public static Int32 ReadInt32(this BufferSlice buffer, int offset)
        {
            return BitConverter.ToInt32(buffer.Array, buffer.Offset + offset);
        }

        public static UInt32 ReadUInt32(this BufferSlice buffer, int offset)
        {
            return BitConverter.ToUInt32(buffer.Array, buffer.Offset + offset);
        }

        public static Int64 ReadInt64(this BufferSlice buffer, int offset)
        {
            return BitConverter.ToInt64(buffer.Array, buffer.Offset + offset);
        }

        public static double ReadDouble(this BufferSlice buffer, int offset)
        {
            return BitConverter.ToDouble(buffer.Array, buffer.Offset + offset);
        }

        public static Decimal ReadDecimal(this BufferSlice buffer, int offset)
        {
            var a = buffer.ReadInt32(offset);
            var b = buffer.ReadInt32(offset + 4);
            var c = buffer.ReadInt32(offset + 8);
            var d = buffer.ReadInt32(offset + 12);
            return new Decimal(new int[] { a, b, c, d });
        }

        public static ObjectId ReadObjectId(this BufferSlice buffer, int offset)
        {
            return new ObjectId(buffer.Array, buffer.Offset + offset);
        }

        public static Guid ReadGuid(this BufferSlice buffer, int offset)
        {
            return new Guid(buffer.ReadBytes(offset, 16));
        }

        public static byte[] ReadBytes(this BufferSlice buffer, int offset, int count)
        {
            var bytes = new byte[count];

            Buffer.BlockCopy(buffer.Array, buffer.Offset + offset, bytes, 0, count);

            return bytes;
        }

        public static DateTime ReadDateTime(this BufferSlice buffer, int offset)
        {
            var ticks = buffer.ReadInt64(offset);

            if (ticks == 0) return DateTime.MinValue;
            if (ticks == 3155378975999999999) return DateTime.MaxValue;

            return new DateTime(ticks, DateTimeKind.Utc);
        }

        public static PageAddress ReadPageAddress(this BufferSlice buffer, int offset)
        {
            return new PageAddress(buffer.ReadUInt32(offset), buffer[offset + 4]);
        }

        public static string ReadString(this BufferSlice buffer, int offset, int count)
        {
            return Encoding.UTF8.GetString(buffer.Array, buffer.Offset + offset, count);
        }

        /// <summary>
        /// Read string with \0 on end. Returns full string length (including \0 char)
        /// </summary>
        public static string ReadCString(this BufferSlice buffer, int offset, out int length)
        {
            length = buffer.Count - buffer.Offset - offset;

            for (var i = offset + buffer.Offset; i < buffer.Count; i++)
            {
                if (buffer[i] == '\0')
                {
                    length = i - buffer.Offset - offset + 1; // +1 for \0
                    break;
                }
            }

            return Encoding.UTF8.GetString(buffer.Array, buffer.Offset + offset, length - 1);
        }

        /// <summary>
        /// Read any BsonValue. Use 1 byte for data type, 1 byte for length (optional), 0-255 bytes to value. 
        /// For document or array, use BufferReader
        /// </summary>
        public static BsonValue ReadIndexKey(this BufferSlice buffer, int offset)
        {
            ExtendedLengthHelper.ReadLength(buffer[offset++], buffer[offset], out var type, out var len);

            switch (type)
            {
                case BsonType.Null: return BsonValue.Null;

                case BsonType.Int32: return buffer.ReadInt32(offset);
                case BsonType.Int64: return buffer.ReadInt64(offset);
                case BsonType.Double: return buffer.ReadDouble(offset);
                case BsonType.Decimal: return buffer.ReadDecimal(offset);

                case BsonType.String:
                    offset++; // for byte length
                    return buffer.ReadString(offset, len);

                case BsonType.Document:
                    using (var r = new BufferReader(buffer))
                    {
                        r.Skip(offset); // skip first byte for value.Type
                        return r.ReadDocument().GetValue();
                    }
                case BsonType.Array:
                    using (var r = new BufferReader(buffer))
                    {
                        r.Skip(offset); // skip first byte for value.Type
                        return r.ReadArray().GetValue();
                    }

                case BsonType.Binary:
                    offset++; // for byte length
                    return buffer.ReadBytes(offset, len);
                case BsonType.ObjectId: return buffer.ReadObjectId(offset);
                case BsonType.Guid: return buffer.ReadGuid(offset);

                case BsonType.Boolean: return buffer[offset] != 0;
                case BsonType.DateTime: return buffer.ReadDateTime(offset);

                case BsonType.MinValue: return BsonValue.MinValue;
                case BsonType.MaxValue: return BsonValue.MaxValue;

                default: throw new NotImplementedException();
            }
        }

        #endregion

        #region Write Extensions

        public static void Write(this BufferSlice buffer, bool value, int offset)
        {
            buffer.Array[buffer.Offset + offset] = value ? (byte)1 : (byte)0;
        }

        public static void Write(this BufferSlice buffer, byte value, int offset)
        {
            buffer.Array[buffer.Offset + offset] = value;
        }

        public static void Write(this BufferSlice buffer, Int16 value, int offset)
        {
            value.ToBytes(buffer.Array, buffer.Offset + offset);
        }

        public static void Write(this BufferSlice buffer, UInt16 value, int offset)
        {
            value.ToBytes(buffer.Array, buffer.Offset + offset);
        }

        public static void Write(this BufferSlice buffer, Int32 value, int offset)
        {
            value.ToBytes(buffer.Array, buffer.Offset + offset);
        }

        public static void Write(this BufferSlice buffer, UInt32 value, int offset)
        {
            value.ToBytes(buffer.Array, buffer.Offset + offset);
        }

        public static void Write(this BufferSlice buffer, Int64 value, int offset)
        {
            value.ToBytes(buffer.Array, buffer.Offset + offset);
        }

        public static void Write(this BufferSlice buffer, Double value, int offset)
        {
            value.ToBytes(buffer.Array, buffer.Offset + offset);
        }

        public static void Write(this BufferSlice buffer, Decimal value, int offset)
        {
            var bits = Decimal.GetBits(value);
            buffer.Write(bits[0], offset);
            buffer.Write(bits[1], offset + 4);
            buffer.Write(bits[2], offset + 8);
            buffer.Write(bits[3], offset + 12);
        }

        public static void Write(this BufferSlice buffer, DateTime value, int offset)
        {
            value.ToUniversalTime().Ticks.ToBytes(buffer.Array, buffer.Offset + offset);
        }

        public static void Write(this BufferSlice buffer, PageAddress value, int offset)
        {
            value.PageID.ToBytes(buffer.Array, buffer.Offset + offset);
            buffer[offset + 4] = value.Index;
        }

        public static void Write(this BufferSlice buffer, Guid value, int offset)
        {
            buffer.Write(value.ToByteArray(), offset);
        }

        public static void Write(this BufferSlice buffer, ObjectId value, int offset)
        {
            value.ToByteArray(buffer.Array, buffer.Offset + offset);
        }

        public static void Write(this BufferSlice buffer, byte[] value, int offset)
        {
            Buffer.BlockCopy(value, 0, buffer.Array, buffer.Offset + offset, value.Length);
        }

        public static void Write(this BufferSlice buffer, string value, int offset)
        {
            Encoding.UTF8.GetBytes(value, 0, value.Length, buffer.Array, buffer.Offset + offset);
        }

        /// <summary>
        /// Wrtie any BsonValue. Use 1 byte for data type, 1 byte for length (optional), 0-255 bytes to value. 
        /// For document or array, use BufferWriter
        /// </summary>
        public static void WriteIndexKey(this BufferSlice buffer, BsonValue value, int offset)
        {
            DEBUG(IndexNode.GetKeyLength(value, true) <= MAX_INDEX_KEY_LENGTH, $"index key must have less than {MAX_INDEX_KEY_LENGTH} bytes");

            if (value.IsString)
            {
                var str = value.AsString;
                var strLength = (ushort)Encoding.UTF8.GetByteCount(str);

                ExtendedLengthHelper.WriteLength(BsonType.String, strLength, out var typeByte, out var lengthByte);

                buffer[offset++] = typeByte;
                buffer[offset++] = lengthByte;
                buffer.Write(str, offset);
            }
            else if(value.IsBinary)
            {
                var arr = value.AsBinary;

                ExtendedLengthHelper.WriteLength(BsonType.Binary, (ushort)arr.Length, out var typeByte, out var lengthByte);

                buffer[offset++] = typeByte;
                buffer[offset++] = lengthByte;
                buffer.Write(arr, offset);
            }
            else
            {
                buffer[offset++] = (byte)value.Type;

                switch (value.Type)
                {
                    case BsonType.Null:
                    case BsonType.MinValue:
                    case BsonType.MaxValue:
                        break;

                    case BsonType.Int32: buffer.Write(value.AsInt32, offset); break;
                    case BsonType.Int64: buffer.Write(value.AsInt64, offset); break;
                    case BsonType.Double: buffer.Write(value.AsDouble, offset); break;
                    case BsonType.Decimal: buffer.Write(value.AsDecimal, offset); break;

                    case BsonType.Document:
                        using (var w = new BufferWriter(buffer))
                        {
                            w.Skip(offset); // skip offset from buffer
                            w.WriteDocument(value.AsDocument, true);
                        }
                        break;
                    case BsonType.Array:
                        using (var w = new BufferWriter(buffer))
                        {
                            w.Skip(offset); // skip offset from buffer
                            w.WriteArray(value.AsArray, true);
                        }
                        break;

                    case BsonType.ObjectId: buffer.Write(value.AsObjectId, offset); break;
                    case BsonType.Guid: buffer.Write(value.AsGuid, offset); break;

                    case BsonType.Boolean: buffer[offset] = (value.AsBoolean) ? (byte)1 : (byte)0; break;
                    case BsonType.DateTime: buffer.Write(value.AsDateTime, offset); break;

                    default: throw new NotImplementedException();
                }
            }
        }

        #endregion
    }
}