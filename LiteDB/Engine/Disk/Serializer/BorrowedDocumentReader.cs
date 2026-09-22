using System;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Traverses a segmented BSON document and decodes only registered scalar paths.
    /// The reader and returned values are scoped to one synchronous evaluation.
    /// </summary>
    internal sealed class BorrowedDocumentReader : IDisposable
    {
        private readonly BorrowedFieldPath[] _paths;
        private readonly bool _utcDate;
        private readonly BufferReader _reader;
        private bool _requiresFallback;

        public BorrowedDocumentReader(BorrowedFieldPath[] paths, bool utcDate, DataService data)
        {
            _paths = paths;
            _utcDate = utcDate;
            _reader = new BufferReader(data, utcDate);
        }

        public bool Read(PageAddress address, BorrowedValueBuffer values)
        {
            _reader.Reset(address);
            _requiresFallback = false;
            var active = _paths.Length == 64 ? UInt64.MaxValue : (1UL << _paths.Length) - 1;
            var found = 0UL;

            this.ReadDocument(_reader, active, 0, values, ref found);
            return !_requiresFallback;
        }

        public void Dispose() => _reader.Dispose();

        private void ReadDocument(BufferReader reader, ulong active, int depth,
            BorrowedValueBuffer values, ref ulong found)
        {
            ENSURE(depth < BufferReader.MAX_BSON_NESTING_DEPTH,
                "BSON nesting depth exceeds the supported limit");
            var length = reader.ReadInt32();
            ENSURE(length >= 5 && length <= MAX_DOCUMENT_SIZE,
                "BSON document length must include its terminator and stay within the document limit");

            var end = (long)reader.Position + length - 5;
            ENSURE(end <= int.MaxValue, "BSON document length exceeds the supported buffer range");

            while (reader.Position < end)
            {
                var type = reader.ReadByte();
                ENSURE(type != 0, "unexpected BSON terminator");

                var matches = this.ReadFieldMask(reader, active, depth);
                var terminal = 0UL;
                var children = 0UL;

                for (var bits = matches; bits != 0; bits &= bits - 1)
                {
                    var index = TrailingZeroCount(bits);
                    var bit = 1UL << index;

                    if (_paths[index].Segments.Length == depth + 1)
                    {
                        if ((found & bit) == 0) terminal |= bit;
                    }
                    else
                    {
                        children |= bit;
                    }
                }

                if ((type == 0x03) && children != 0)
                {
                    this.SetTerminalTypes(terminal, BsonType.Document, values, ref found);
                    this.ReadDocument(reader, children, depth + 1, values, ref found);
                }
                else
                {
                    this.ReadOrSkipValue(reader, type, terminal, depth, end, values, ref found);
                }
            }

            ENSURE(reader.Position == end, "BSON element exceeds document length");
            ENSURE(reader.ReadByte() == 0, "missing BSON document terminator");
        }

        private ulong ReadFieldMask(BufferReader reader, ulong active, int depth)
        {
            var matches = active;
            var position = 0;

            while (true)
            {
                var value = reader.ReadByte();

                if (value == 0) break;

                if (value > 0x7F && matches != 0)
                {
                    // UTF-8 byte folding is not equivalent to OrdinalIgnoreCase
                    // for all Unicode characters (for example, the Kelvin sign).
                    _requiresFallback = true;
                }

                for (var bits = matches; bits != 0; bits &= bits - 1)
                {
                    var index = TrailingZeroCount(bits);
                    var expected = _paths[index].EncodedSegments[depth];

                    if (position >= expected.Length || !AsciiEqualsIgnoreCase(value, expected[position]))
                    {
                        matches &= ~(1UL << index);
                    }
                }

                position++;
            }

            for (var bits = matches; bits != 0; bits &= bits - 1)
            {
                var index = TrailingZeroCount(bits);

                if (_paths[index].EncodedSegments[depth].Length != position)
                {
                    matches &= ~(1UL << index);
                }
            }

            return matches;
        }

        private void ReadOrSkipValue(BufferReader reader, byte type, ulong terminal, int depth, long containerEnd,
            BorrowedValueBuffer values, ref ulong found)
        {
            if (terminal == 0)
            {
                BsonElementReader.SkipValue(reader, type, depth + 1, containerEnd);
                return;
            }

            BorrowedBsonValue value;

            switch (type)
            {
                case 0x01:
                    value = BorrowedBsonValue.FromDouble(reader.ReadDouble());
                    break;
                case 0x02:
                    var stringLength = reader.ReadInt32();
                    ENSURE(stringLength >= 1 && stringLength <= MAX_DOCUMENT_SIZE,
                        "BSON string length must include a terminator and stay within the document limit");
                    value = BorrowedBsonValue.FromString(reader.ReadString(stringLength - 1));
                    ENSURE(reader.ReadByte() == 0, "missing BSON string terminator");
                    break;
                case 0x03:
                    value = BorrowedBsonValue.FromType(BsonType.Document);
                    BsonElementReader.SkipValue(reader, type, depth + 1, containerEnd);
                    break;
                case 0x04:
                    value = BorrowedBsonValue.FromType(BsonType.Array);
                    BsonElementReader.SkipValue(reader, type, depth + 1, containerEnd);
                    break;
                case 0x05:
                    value = this.ReadBinary(reader, containerEnd);
                    break;
                case 0x07:
                    value = BorrowedBsonValue.FromType(BsonType.ObjectId);
                    reader.Skip(12);
                    break;
                case 0x08:
                    value = BorrowedBsonValue.FromBoolean(reader.ReadBoolean());
                    break;
                case 0x09:
                    value = BorrowedBsonValue.FromDateTime(this.ReadDateTime(reader.ReadInt64()));
                    break;
                case 0x0A:
                    value = BorrowedBsonValue.Null;
                    break;
                case 0x10:
                    value = BorrowedBsonValue.FromInt32(reader.ReadInt32());
                    break;
                case 0x12:
                    value = BorrowedBsonValue.FromInt64(reader.ReadInt64());
                    break;
                case 0x13:
                    value = BorrowedBsonValue.FromDecimal(reader.ReadDecimal());
                    break;
                case 0x7F:
                    value = BorrowedBsonValue.FromType(BsonType.MaxValue);
                    break;
                case 0xFF:
                    value = BorrowedBsonValue.FromType(BsonType.MinValue);
                    break;
                case 0x64:
                    value = BorrowedBsonValue.FromType(BsonType.Vector);
                    reader.Skip(checked(reader.ReadUInt16() * 4));
                    break;
                default:
                    throw new NotSupportedException("BSON type not supported");
            }

            this.SetTerminalValues(terminal, value, values, ref found);
        }

        private BorrowedBsonValue ReadBinary(BufferReader reader, long containerEnd)
        {
            var length = BsonElementReader.ReadBinaryHeader(reader, out var subtype, containerEnd);

            if (subtype == 0x04)
            {
                return BorrowedBsonValue.FromGuid(reader.ReadGuid());
            }

            reader.Skip(length);
            return BorrowedBsonValue.FromType(BsonType.Binary);
        }

        private DateTime ReadDateTime(long timestamp)
        {
            if (timestamp == 253402300800000) return DateTime.MaxValue;
            if (timestamp == -62135596800000) return DateTime.MinValue;

            var date = BsonValue.UnixEpoch.AddMilliseconds(timestamp);
            return _utcDate ? date : date.ToLocalTime();
        }

        private void SetTerminalTypes(ulong terminal, BsonType type,
            BorrowedValueBuffer values, ref ulong found)
        {
            this.SetTerminalValues(terminal, BorrowedBsonValue.FromType(type), values, ref found);
        }

        private void SetTerminalValues(ulong terminal, BorrowedBsonValue value,
            BorrowedValueBuffer values, ref ulong found)
        {
            for (var bits = terminal; bits != 0; bits &= bits - 1)
            {
                var index = TrailingZeroCount(bits);
                values[_paths[index].Slot] = value;
            }

            found |= terminal;
        }

        private static bool AsciiEqualsIgnoreCase(byte left, byte right)
        {
            if (left >= (byte)'A' && left <= (byte)'Z') left += (byte)('a' - 'A');
            if (right >= (byte)'A' && right <= (byte)'Z') right += (byte)('a' - 'A');
            return left == right;
        }

        private static int TrailingZeroCount(ulong value)
        {
            var count = 0;

            while ((value & 1) == 0)
            {
                value >>= 1;
                count++;
            }

            return count;
        }
    }
}
