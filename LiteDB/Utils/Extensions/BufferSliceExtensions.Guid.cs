using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LiteDB.Engine;
using static LiteDB.Constants;

namespace LiteDB
{
    internal static partial class BufferSliceExtensions
    {
        public static Guid ReadGuid(this BufferSlice buffer, int offset)
        {
            buffer.EnsureReadable();
            var span = new ReadOnlySpan<byte>(buffer.Array, buffer.Offset + offset, 16);

            return ReadGuid(span);
        }

        internal static Guid ReadGuid(ReadOnlySpan<byte> span)
        {
            ENSURE(span.Length >= 16, "span must contain at least 16 bytes");

            var a =
                span[0] |
                (span[1] << 8) |
                (span[2] << 16) |
                (span[3] << 24);

            var b = (short)(span[4] | (span[5] << 8));
            var c = (short)(span[6] | (span[7] << 8));

            return new Guid(a, b, c,
                span[8], span[9], span[10], span[11],
                span[12], span[13], span[14], span[15]);
        }

        public static void Write(this BufferSlice buffer, Guid value, int offset)
        {
            buffer.EnsureWritable();
            var span = new Span<byte>(buffer.Array, buffer.Offset + offset, 16);

            Write(span, value);
        }

        internal static void Write(Span<byte> destination, Guid value)
        {
            ENSURE(destination.Length >= 16, "span must contain at least 16 bytes");

#if NET8_0_OR_GREATER
            if (!value.TryWriteBytes(destination))
            {
                throw new InvalidOperationException("Failed to write Guid into span.");
            }
#else
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);

            if (!BitConverter.IsLittleEndian)
            {
                ReverseGuidFields(destination);
            }
#endif
        }

#if !NET8_0_OR_GREATER
        private static void ReverseGuidFields(Span<byte> buffer)
        {
            Swap(buffer, 0, 3);
            Swap(buffer, 1, 2);
            Swap(buffer, 4, 5);
            Swap(buffer, 6, 7);
        }

        private static void Swap(Span<byte> buffer, int left, int right)
        {
            var value = buffer[left];
            buffer[left] = buffer[right];
            buffer[right] = value;
        }
#endif
    }
}
