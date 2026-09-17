using System;

namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class NativeScalarRecord
{
    public int Id { get; set; }
    public byte ByteValue { get; set; }
    public sbyte SignedByteValue { get; set; }
    public char Character { get; set; }
    public short SignedShort { get; set; }
    public ushort UnsignedShort { get; set; }
    public uint UnsignedInteger { get; set; }
    public ulong UnsignedLong { get; set; }
    public float SingleValue { get; set; }
    public NativeScalarState State { get; set; }
    public ObjectId ObjectId { get; set; } = ObjectId.Empty;
    public DateTime Timestamp { get; set; }
    public DateTimeOffset TimestampWithOffset { get; set; }
    public byte[] Payload { get; set; } = [];
    public Guid CorrelationId { get; set; }
    public string Name { get; set; } = string.Empty;
}
