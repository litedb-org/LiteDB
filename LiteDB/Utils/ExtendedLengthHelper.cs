using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Class to help extend IndexNode key up to 1023 bytes length (for string/byte[]) using 2 first bits in BsonType
    /// </summary>
    internal static class ExtendedLengthHelper
    {
        /// <summary>
        /// Read BsonType and UShort length from 2 bytes
        /// </summary>
        public static void ReadLength(byte typeByte, byte lengthByte, out BsonType type, out ushort length)
        {
            var bsonType = (byte)(typeByte & 0b0011_1111);
            var lengthLSByte = lengthByte;
            var lengthMSByte = (byte)(typeByte & 0b1100_0000);
            type = (BsonType)bsonType;
            length = (ushort)((lengthMSByte << 2) | (lengthLSByte));
        }

        /// <summary>
        /// Write BsonType and UShort length in 2 bytes
        /// </summary>
        public static void WriteLength(BsonType type, ushort length, out byte typeByte, out byte lengthByte)
        {
            if (length > 1023) throw new ArgumentOutOfRangeException(nameof(length));
            var bsonType = (byte)type;
            var lengthLSByte = unchecked((byte)length);
            var lengthMSByte = (byte)((length & 0b11_0000_0000) >> 2);
            typeByte = (byte)(lengthMSByte | bsonType);
            lengthByte = lengthLSByte;
        }
    }
}
