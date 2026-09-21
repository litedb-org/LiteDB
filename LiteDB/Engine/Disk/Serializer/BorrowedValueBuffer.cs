using System;
using System.Buffers;
using System.Runtime.InteropServices;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Stores borrowed scalar slots in the process-wide byte pool. Strings stay
    /// visible to the GC in a lazily allocated side table for the current read.
    /// </summary>
    internal sealed class BorrowedValueBuffer : IDisposable
    {
        private const int SlotSize = 24;

        private readonly int _slotCount;
        private byte[] _buffer;
        private string[] _strings;

        public BorrowedValueBuffer(int slotCount)
        {
            ENSURE(slotCount > 0, "borrowed value buffer must contain at least one slot");

            _slotCount = slotCount;
            _buffer = ArrayPool<byte>.Shared.Rent(checked(slotCount * SlotSize));
        }

        public BorrowedBsonValue this[int index]
        {
            get
            {
                this.EnsureIndex(index);
                var text = _strings == null ? null : _strings[index];
                return BorrowedBsonValue.FromSlot(this.Slots[index], text);
            }
            set
            {
                this.EnsureIndex(index);
                value.WriteTo(ref this.Slots[index]);

                if (value.Type == BsonType.String)
                {
                    if (_strings == null) _strings = new string[_slotCount];
                    _strings[index] = value.StringValue;
                }
                else if (_strings != null)
                {
                    _strings[index] = null;
                }
            }
        }

        public void Reset(int count)
        {
            ENSURE(count >= 0 && count <= _slotCount, "invalid borrowed value slot count");

            for (var i = 0; i < count; i++) this[i] = BorrowedBsonValue.Null;
        }

        public void Dispose()
        {
            var buffer = _buffer;
            if (buffer == null) return;

            _buffer = null;
            if (_strings != null) Array.Clear(_strings, 0, _strings.Length);
            _strings = null;
            ArrayPool<byte>.Shared.Return(buffer);
        }

        private Span<BorrowedBsonSlot> Slots =>
            MemoryMarshal.Cast<byte, BorrowedBsonSlot>(
                _buffer.AsSpan(0, checked(_slotCount * SlotSize)));

        private void EnsureIndex(int index)
        {
            if ((uint)index >= (uint)_slotCount)
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct BorrowedBsonSlot
    {
        [FieldOffset(0)] internal long Integer;
        [FieldOffset(0)] internal double Double;
        [FieldOffset(0)] internal decimal Decimal;
        [FieldOffset(0)] internal DateTime DateTime;
        [FieldOffset(0)] internal Guid Guid;
        [FieldOffset(16)] internal BsonType Type;
        [FieldOffset(17)] internal byte IsDecoded;
    }
}
