using System;

using LiteDB;

namespace LiteDB.SourceGenerator.PackageConsumer;

[BsonSourceGenerated]
public sealed class PackagedC2ScalarRecord
{
    public int Id { get; set; }
    public bool BooleanValue { get; set; }
    public uint UnsignedInteger { get; set; }
    public long SignedLong { get; set; }
    public ulong UnsignedLong { get; set; }
    public decimal DecimalValue { get; set; }
    public PackagedScalarState State { get; set; }
    public DateTime Timestamp { get; set; }
    public ObjectId ObjectId { get; set; } = ObjectId.Empty;
    public Guid CorrelationId { get; set; }
    public byte[] Payload { get; set; } = [];
    public string? Name { get; set; }
    public PackagedScalarState? NullableState { get; set; }
    public byte[]? NullablePayload { get; set; }
}
