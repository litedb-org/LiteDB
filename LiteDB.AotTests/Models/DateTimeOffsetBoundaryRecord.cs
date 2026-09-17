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
