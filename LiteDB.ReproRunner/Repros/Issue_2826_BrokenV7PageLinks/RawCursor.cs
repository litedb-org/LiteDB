using System.Buffers.Binary;
using System.Text;

internal sealed class RawCursor
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly byte[] _bytes;
    private readonly int _end;

    public RawCursor(byte[] bytes, int offset, int count)
    {
        _bytes = bytes;
        Position = offset;
        _end = checked(offset + count);
        Require(offset >= 0 && count >= 0 && _end <= bytes.Length, "cursor range is outside the buffer");
    }

    public int Position { get; private set; }

    public int Remaining => _end - Position;

    public byte ReadByte()
    {
        RequireAvailable(1);
        return _bytes[Position++];
    }

    public ushort ReadUInt16()
    {
        RequireAvailable(sizeof(ushort));
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(Position, sizeof(ushort)));
        Position += sizeof(ushort);
        return value;
    }

    public uint ReadUInt32()
    {
        RequireAvailable(sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(Position, sizeof(uint)));
        Position += sizeof(uint);
        return value;
    }

    public int ReadInt32()
    {
        RequireAvailable(sizeof(int));
        var value = BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(Position, sizeof(int)));
        Position += sizeof(int);
        return value;
    }

    public byte[] ReadBytes(int count)
    {
        RequireAvailable(count);
        var result = _bytes.AsSpan(Position, count).ToArray();
        Position += count;
        return result;
    }

    public string ReadSizedString()
    {
        var count = ReadInt32();
        Require(count >= 0, "negative string length");
        return Decode(ReadBytes(count));
    }

    public string ReadCString()
    {
        var start = Position;
        while (ReadByte() != 0)
        {
        }

        return Decode(_bytes.AsSpan(start, Position - start - 1));
    }

    public void Skip(int count)
    {
        RequireAvailable(count);
        Position += count;
    }

    private static string Decode(ReadOnlySpan<byte> bytes) => StrictUtf8.GetString(bytes);

    private void RequireAvailable(int count)
    {
        Require(count >= 0 && Position <= _end - count, "raw fixture field runs past its containing region");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
