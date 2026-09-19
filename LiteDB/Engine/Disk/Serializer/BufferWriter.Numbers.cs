using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LiteDB.Engine
{
    internal partial class BufferWriter
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteUInt16(UInt16 value)
        {
            const int size = sizeof(UInt16);

            if (_currentPosition + size <= _current.Count)
            {
                _current.EnsureWritable();
                value.ToBytes(_current.Array, _current.Offset + _currentPosition);
                this.MoveForward(size);
                return;
            }

            this.WriteUInt16Segmented(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteUInt32(UInt32 value)
        {
            const int size = sizeof(UInt32);

            if (_currentPosition + size <= _current.Count)
            {
                _current.EnsureWritable();
                value.ToBytes(_current.Array, _current.Offset + _currentPosition);
                this.MoveForward(size);
                return;
            }

            this.WriteUInt32Segmented(value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteUInt64(UInt64 value)
        {
            const int size = sizeof(UInt64);

            if (_currentPosition + size <= _current.Count)
            {
                _current.EnsureWritable();
                value.ToBytes(_current.Array, _current.Offset + _currentPosition);
                this.MoveForward(size);
                return;
            }

            this.WriteUInt64Segmented(value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void WriteUInt16Segmented(UInt16 value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(UInt16)];
            WriteUInt16(buffer, value);
            this.WriteFrom(buffer);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void WriteUInt32Segmented(UInt32 value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(UInt32)];
            WriteUInt32(buffer, value);
            this.WriteFrom(buffer);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void WriteUInt64Segmented(UInt64 value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(UInt64)];
            WriteUInt64(buffer, value);
            this.WriteFrom(buffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteUInt16(Span<byte> span, UInt16 value)
        {
            if (BitConverter.IsLittleEndian)
                BinaryPrimitives.WriteUInt16LittleEndian(span, value);
            else
                BinaryPrimitives.WriteUInt16BigEndian(span, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteUInt32(Span<byte> span, UInt32 value)
        {
            if (BitConverter.IsLittleEndian)
                BinaryPrimitives.WriteUInt32LittleEndian(span, value);
            else
                BinaryPrimitives.WriteUInt32BigEndian(span, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteUInt64(Span<byte> span, UInt64 value)
        {
            if (BitConverter.IsLittleEndian)
                BinaryPrimitives.WriteUInt64LittleEndian(span, value);
            else
                BinaryPrimitives.WriteUInt64BigEndian(span, value);
        }

        private static unsafe int SingleToInt32Bits(float value)
        {
            // Unsafe cast lets the Engine serializer stay allocation-free when writing floats.
            return *(int*)&value;
        }

        public void Write(Int32 value) => this.WriteUInt32(unchecked((UInt32)value));
        public void Write(Int64 value) => this.WriteUInt64(unchecked((UInt64)value));
        public void Write(UInt16 value) => this.WriteUInt16(value);
        public void Write(UInt32 value) => this.WriteUInt32(value);
        public void Write(Single value) => this.WriteUInt32(unchecked((UInt32)SingleToInt32Bits(value)));
        public void Write(Double value) => this.WriteUInt64(unchecked((UInt64)BitConverter.DoubleToInt64Bits(value)));

        public void Write(Decimal value)
        {
#if NET8_0_OR_GREATER
            Span<int> bits = stackalloc int[4];
            Decimal.GetBits(value, bits);
#else
            var bits = Decimal.GetBits(value);
#endif
            this.Write(bits[0]);
            this.Write(bits[1]);
            this.Write(bits[2]);
            this.Write(bits[3]);
        }
    }
}
