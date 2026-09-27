using System;

#nullable enable
namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed class AotNullableScalarRecord
{
    public int Id { get; set; }
    public int? ProcessId { get; set; }
    public bool? IsElevated { get; set; }
    public AotNativeScalarState? State { get; set; }
    public Guid? CorrelationId { get; set; }
    public DateTime? RecordedAt { get; set; }
}
