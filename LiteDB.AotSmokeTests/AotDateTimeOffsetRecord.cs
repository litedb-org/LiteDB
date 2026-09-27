using System;

#nullable enable
namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed class AotDateTimeOffsetRecord
{
    public int Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
