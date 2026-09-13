using System;
using System.Linq;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB
{
    internal static class BufferExtensions
    {
        // https://code.google.com/p/freshbooks-api/source/browse/depend/NClassify.Generator/content/ByteArray.cs?r=bbb6c13ec7a01eae082796550f1ddc05f61694b8
        public static int BinaryCompareTo(this byte[] lh, byte[] rh)
        {
            if (lh == null) return rh == null ? 0 : -1;
            if (rh == null) return 1;

            var result = 0;
            var i = 0;
            var stop = Math.Min(lh.Length, rh.Length);

            for (; 0 == result && i < stop; i++)
                result = lh[i].CompareTo(rh[i]);

            if (result != 0) return result < 0 ? -1 : 1;
            if (i == lh.Length) return i == rh.Length ? 0 : -1;
            return 1;
        }

        /// <summary>
        /// Very fast way to check if all byte array is full of zero
        /// </summary>
        public static unsafe bool IsFullZero(this byte[] data)
        {
            fixed (byte* bytes = data)
            {
                int len = data.Length;
                int rem = len % (sizeof(long) * 16);
                long* b = (long*)bytes;
                long* e = (long*)(bytes + len - rem);

                while (b < e)
                {
                    if ((*(b) | *(b + 1) | *(b + 2) | *(b + 3) | *(b + 4) |
                        *(b + 5) | *(b + 6) | *(b + 7) | *(b + 8) |
                        *(b + 9) | *(b + 10) | *(b + 11) | *(b + 12) |
                        *(b + 13) | *(b + 14) | *(b + 15)) != 0)
                        return false;
                    b += 16;
                }

                for (int i = 0; i < rem; i++)
                    if (data[len - 1 - i] != 0)
                        return false;

                return true;
            }
        }

        /// <summary>
        /// Fill all array with defined value
        /// </summary>
        public static byte[] Fill(this byte[] array, byte value, int offset, int count)
        {
            for(var i = 0; i < count; i++)
            {
                array[i + offset] = value;
            }

            return array;
        }

        /// <summary>
        /// Read UTF8 string until found \0
        /// </summary>
        public static string ReadCString(this byte[] bytes, int startIndex, out int bytesCount)
        {
            var position = Array.IndexOf(bytes, (byte)0x00, startIndex);

            if (position > 0)
            {
                bytesCount = position - startIndex;

                var str = Encoding.UTF8.GetString(bytes, startIndex, bytesCount);

                return str;
            }

            bytesCount = 0;
            return null;
        }

        #region ToBytes BitConverter helper

        /// <summary>
        /// Copy Int16 bytes direct into buffer
        /// </summary>
        public static unsafe void ToBytes(this Int16 value, byte[] array, int startIndex)
        {
            fixed (byte* ptr = &array[startIndex])
            {
                *(Int16*)ptr = value;
            }
        }

        /// <summary>
        /// Copy Int32 bytes direct into buffer
        /// </summary>
        public static unsafe void ToBytes(this Int32 value, byte[] array, int startIndex)
        {
            fixed (byte* ptr = &array[startIndex])
            {
                *(Int32*)ptr = value;
            }
        }

        /// <summary>
        /// Copy Int64 bytes direct into buffer
        /// </summary>
        public static void ToBytes(this Int64 value, byte[] array, int startIndex)
        {
            unchecked
            {
                ToBytes((UInt64)value, array, startIndex);
            }
        }

        /// <summary>
        /// Copy UInt16 bytes direct into buffer
        /// </summary>
        public static unsafe void ToBytes(this UInt16 value, byte[] array, int startIndex)
        {
            fixed (byte* ptr = &array[startIndex])
            {
                *(UInt16*)ptr = value;
            }
        }

        /// <summary>
        /// Copy UInt32 bytes direct into buffer
        /// </summary>
        public static unsafe void ToBytes(this UInt32 value, byte[] array, int startIndex)
        {
            fixed (byte* ptr = &array[startIndex])
            {
                *(UInt32*)ptr = value;
            }
        }

        /// <summary>
        /// Copy Int64 bytes direct into buffer
        /// </summary>
        public static void ToBytes(this UInt64 value, byte[] array, int startIndex)
        {
            // A single `*(UInt64*)ptr = value` store is lowered to STRD/STM on 32-bit ARM,
            // which fault with SIGBUS/BUS_ADRALN when the destination is not word-aligned
            // (see #1759). Byte stores have no alignment requirement.
            //
            // The byte order follows the running platform, so the bytes written here are
            // identical to the ones the previous pointer store produced. That keeps the
            // on-disk layout unchanged: the matching readers (ReadInt64/ReadUInt64/
            // ReadDouble) use BitConverter, which is also native-endian.
            unchecked
            {
                if (BitConverter.IsLittleEndian)
                {
                    array[startIndex] = (byte)value;
                    array[startIndex + 1] = (byte)(value >> 8);
                    array[startIndex + 2] = (byte)(value >> 16);
                    array[startIndex + 3] = (byte)(value >> 24);
                    array[startIndex + 4] = (byte)(value >> 32);
                    array[startIndex + 5] = (byte)(value >> 40);
                    array[startIndex + 6] = (byte)(value >> 48);
                    array[startIndex + 7] = (byte)(value >> 56);
                }
                else
                {
                    array[startIndex] = (byte)(value >> 56);
                    array[startIndex + 1] = (byte)(value >> 48);
                    array[startIndex + 2] = (byte)(value >> 40);
                    array[startIndex + 3] = (byte)(value >> 32);
                    array[startIndex + 4] = (byte)(value >> 24);
                    array[startIndex + 5] = (byte)(value >> 16);
                    array[startIndex + 6] = (byte)(value >> 8);
                    array[startIndex + 7] = (byte)value;
                }
            }
        }

        /// <summary>
        /// Copy Single bytes direct into buffer
        /// </summary>
        public static unsafe void ToBytes(this Single value, byte[] array, int startIndex)
        {
            ToBytes(*(UInt32*)(&value), array, startIndex);

            //fixed (byte* ptr = &array[startIndex])
            //{
            //    *(Single*)ptr = value;
            //}
        }

        /// <summary>
        /// Copy Double bytes direct into buffer
        /// </summary>
        public static unsafe void ToBytes(this Double value, byte[] array, int startIndex)
        {
            ToBytes(*(UInt64*)(&value), array, startIndex);

            //fixed (byte* ptr = &array[startIndex])
            //{
            //    *(Double*)ptr = value;
            //}
        }

        #endregion
    }
}