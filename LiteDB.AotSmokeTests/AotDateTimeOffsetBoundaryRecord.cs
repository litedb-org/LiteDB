using System;

#nullable enable
namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed class AotDateTimeOffsetBoundaryRecord
{
    public int Id { get; set; }
    public DateTimeOffset PositiveOffset { get; set; }
    public DateTimeOffset NegativeOffset { get; set; }
    public DateTimeOffset? NullableOffset { get; set; }
}
