using System;

#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class DateTimeOffsetRecord
{
    public int Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
