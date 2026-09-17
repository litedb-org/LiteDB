using System;

#nullable enable
namespace LiteDB.AotTests;

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
