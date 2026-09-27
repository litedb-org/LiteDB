using System;
using System.Collections.Generic;

#nullable enable
namespace LiteDB.AotTests;

public class ManualGeneratedRecordBase
{
}

[BsonSourceGenerated]
public sealed class ManualGeneratedRecord : ManualGeneratedRecordBase
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Score { get; set; }
    public Dictionary<string, object?> LegacyProbe { get; set; } = [];
}

[BsonSourceGenerated]
public sealed class AttributedScalarRecord
{
    public int Id { get; set; }

    [BsonField("score")]
    public int Score { get; set; }
}

[BsonSourceGenerated]
public sealed class ScalarCompatibilityRecord
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
    public GeneratedScalarState State { get; set; }
    public DateTime Timestamp { get; set; }
    public ObjectId ObjectId { get; set; } = ObjectId.Empty;
    public Guid CorrelationId { get; set; }
    public byte[] Payload { get; set; } = [];
    public string? Name { get; set; }
    public int? NullableInteger { get; set; }
    public GeneratedScalarState? NullableState { get; set; }
    public ObjectId? NullableObjectId { get; set; }
    public Guid? NullableCorrelationId { get; set; }
    public DateTime? NullableTimestamp { get; set; }
    public byte[]? NullablePayload { get; set; }
}

[BsonSourceGenerated]
public sealed class GeneratedScalarRecord
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public int Score { get; set; }
}

public enum GeneratedScalarState
{
    Ready = 1,
    Completed = 5
}
