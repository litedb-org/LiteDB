using System;

#nullable enable
namespace LiteDB.AotTests;

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
