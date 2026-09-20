using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace LiteDB.Engine
{
    internal partial class BufferReader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private UInt16 ReadUInt16Number()
        {
            const int size = sizeof(UInt16);

            if (_currentPosition + size <= _current.Count)
            {
                _current.EnsureReadable();
                var span = new ReadOnlySpan<byte>(_current.Array, _current.Offset + _currentPosition, size);
                var value = ReadUInt16(span);
                this.MoveForward(size);
                return value;
            }

            return this.ReadUInt16Segmented();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private UInt32 ReadUInt32Number()
        {
            const int size = sizeof(UInt32);

            if (_currentPosition + size <= _current.Count)
            {
                _current.EnsureReadable();
                var span = new ReadOnlySpan<byte>(_current.Array, _current.Offset + _currentPosition, size);
                var value = ReadUInt32(span);
                this.MoveForward(size);
                return value;
            }

            return this.ReadUInt32Segmented();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private UInt64 ReadUInt64Number()
        {
            const int size = sizeof(UInt64);

            if (_currentPosition + size <= _current.Count)
            {
                _current.EnsureReadable();
                var span = new ReadOnlySpan<byte>(_current.Array, _current.Offset + _currentPosition, size);
                var value = ReadUInt64(span);
                this.MoveForward(size);
                return value;
            }

            return this.ReadUInt64Segmented();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private UInt16 ReadUInt16Segmented()
        {
            Span<byte> buffer = stackalloc byte[sizeof(UInt16)];
            this.ReadInto(buffer);
            return ReadUInt16(buffer);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private UInt32 ReadUInt32Segmented()
        {
            Span<byte> buffer = stackalloc byte[sizeof(UInt32)];
            this.ReadInto(buffer);
            return ReadUInt32(buffer);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private UInt64 ReadUInt64Segmented()
        {
            Span<byte> buffer = stackalloc byte[sizeof(UInt64)];
            this.ReadInto(buffer);
            return ReadUInt64(buffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UInt16 ReadUInt16(ReadOnlySpan<byte> span) => BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(span)
            : BinaryPrimitives.ReadUInt16BigEndian(span);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UInt32 ReadUInt32(ReadOnlySpan<byte> span) => BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(span)
            : BinaryPrimitives.ReadUInt32BigEndian(span);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static UInt64 ReadUInt64(ReadOnlySpan<byte> span) => BitConverter.IsLittleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(span)
            : BinaryPrimitives.ReadUInt64BigEndian(span);

        private static unsafe float Int32BitsToSingle(int value)
        {
            // Unsafe re-interpretation keeps Engine serializer span-based without extra allocations.
            return *(float*)&value;
        }

        public Int32 ReadInt32() => unchecked((Int32)this.ReadUInt32Number());
        public Int64 ReadInt64() => unchecked((Int64)this.ReadUInt64Number());
        public UInt16 ReadUInt16() => this.ReadUInt16Number();
        public UInt32 ReadUInt32() => this.ReadUInt32Number();
        public Single ReadSingle() => Int32BitsToSingle(unchecked((Int32)this.ReadUInt32Number()));
        public Double ReadDouble() => BitConverter.Int64BitsToDouble(unchecked((Int64)this.ReadUInt64Number()));

        public Decimal ReadDecimal()
        {
#if NET8_0_OR_GREATER
            Span<int> bits = stackalloc int[4]
            {
                this.ReadInt32(),
                this.ReadInt32(),
                this.ReadInt32(),
                this.ReadInt32()
            };
            return new Decimal(bits);
#else
            var a = this.ReadInt32();
            var b = this.ReadInt32();
            var c = this.ReadInt32();
            var d = this.ReadInt32();
            return new Decimal(new int[] { a, b, c, d });
#endif
        }
    }
}
