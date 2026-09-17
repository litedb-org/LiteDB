using System;

#nullable enable
namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed class AotNullableScalarBoundaryRecord
{
    public int Id { get; set; }
    public short? SignedShort { get; set; }
    public ulong? UnsignedLong { get; set; }
    public double? Ratio { get; set; }
    public decimal? Amount { get; set; }
    public DateTimeOffset? TimestampWithOffset { get; set; }
}
