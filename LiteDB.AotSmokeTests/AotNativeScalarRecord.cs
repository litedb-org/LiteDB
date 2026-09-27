using System;

namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed class AotNativeScalarRecord
{
    public int Id { get; set; }
    public bool BooleanValue { get; set; }
    public byte ByteValue { get; set; }
    public sbyte SignedByteValue { get; set; }
    public char Character { get; set; }
    public short SignedShort { get; set; }
    public ushort UnsignedShort { get; set; }
    public int SignedInteger { get; set; }
    public uint UnsignedInteger { get; set; }
    public long SignedLong { get; set; }
    public ulong UnsignedLong { get; set; }
    public float SingleValue { get; set; }
    public double DoubleValue { get; set; }
    public decimal DecimalValue { get; set; }
    public AotNativeScalarState State { get; set; }
    public ObjectId ObjectId { get; set; } = ObjectId.Empty;
    public DateTime Timestamp { get; set; }
    public DateTimeOffset TimestampWithOffset { get; set; }
    public byte[] Payload { get; set; } = [];
    public Guid CorrelationId { get; set; }
    public string Name { get; set; } = string.Empty;
}
