using System;

#nullable enable
namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed class AotScalarCompatibilityRecord
{
    public int Id { get; set; }
    public bool BooleanValue { get; set; }
    public uint UnsignedInteger { get; set; }
    public long SignedLong { get; set; }
    public ulong UnsignedLong { get; set; }
    public decimal DecimalValue { get; set; }
    public AotNativeScalarState State { get; set; }
    public DateTime Timestamp { get; set; }
    public ObjectId ObjectId { get; set; } = ObjectId.Empty;
    public Guid CorrelationId { get; set; }
    public byte[] Payload { get; set; } = [];
    public string Name { get; set; } = string.Empty;
    public AotNativeScalarState? NullableState { get; set; }
    public byte[]? NullablePayload { get; set; }
}
