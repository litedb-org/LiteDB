using System;

#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class PhaseCScalarCompatibilityRecord
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
    public PhaseCScalarState State { get; set; }
    public DateTime Timestamp { get; set; }
    public ObjectId ObjectId { get; set; } = ObjectId.Empty;
    public Guid CorrelationId { get; set; }
    public byte[] Payload { get; set; } = [];
    public string? Name { get; set; }
    public int? NullableInteger { get; set; }
    public PhaseCScalarState? NullableState { get; set; }
    public ObjectId? NullableObjectId { get; set; }
    public Guid? NullableCorrelationId { get; set; }
    public DateTime? NullableTimestamp { get; set; }
    public byte[]? NullablePayload { get; set; }
}
