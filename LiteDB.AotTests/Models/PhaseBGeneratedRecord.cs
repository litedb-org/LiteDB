using System.Collections.Generic;

#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class PhaseBGeneratedRecord : PhaseBGeneratedRecordBase
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Score { get; set; }
    public Dictionary<string, object?> LegacyProbe { get; set; } = [];
}
