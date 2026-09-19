using System;

#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class DateTimeOffsetBoundaryRecord
{
    public int Id { get; set; }
    public DateTimeOffset PositiveOffset { get; set; }
    public DateTimeOffset NegativeOffset { get; set; }
    public DateTimeOffset? NullableOffset { get; set; }
    public DateTimeOffset Minimum { get; set; }
    public DateTimeOffset Maximum { get; set; }
}

[BsonSourceGenerated]
public sealed class DateTimeOffsetRecord
{
    public int Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

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

public enum NativeScalarState
{
    Unknown = 0,
    Captured = 17
}

[BsonSourceGenerated]
public sealed class NullableGuidIdRecord
{
    public Guid? Id { get; set; }
}

[BsonSourceGenerated]
public sealed class NullableIntIdRecord
{
    public int? Id { get; set; }
}

[BsonSourceGenerated]
public sealed class NullableLongIdRecord
{
    public long? Id { get; set; }
}

[BsonSourceGenerated]
public sealed class NullableScalarBoundaryRecord
{
    public int Id { get; set; }
    public short? SignedShort { get; set; }
    public ulong? UnsignedLong { get; set; }
    public double? Ratio { get; set; }
    public decimal? Amount { get; set; }
    public DateTimeOffset? TimestampWithOffset { get; set; }
}

[BsonSourceGenerated]
public sealed class NullableScalarRecord
{
    public int Id { get; set; }
    public int? ProcessId { get; set; }
    public bool? IsElevated { get; set; }
    public NativeScalarState? State { get; set; }
    public Guid? CorrelationId { get; set; }
    public DateTime? RecordedAt { get; set; }
}

[BsonSourceGenerated]
public sealed class ScalarRecord
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    public int Count { get; set; }
    public long Total { get; set; }
    public double Ratio { get; set; }
    public decimal Amount { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid CorrelationId { get; set; }
    public byte[] Payload { get; set; } = [];
}

public enum SignedWideState : long
{
    BeyondInt32 = 5_000_000_000L
}

public enum UnsignedWideState : ulong
{
    NearMaximum = ulong.MaxValue - 3
}

[BsonSourceGenerated]
public sealed class WideEnumRecord
{
    public int Id { get; set; }
    public SignedWideState Signed { get; set; }
    public UnsignedWideState Unsigned { get; set; }
    public SignedWideState? NullableSigned { get; set; }
}
