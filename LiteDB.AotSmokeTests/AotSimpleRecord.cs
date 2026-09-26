using System.Collections.Generic;

namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed class AotSimpleRecord : AotSimpleRecordBase
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Score { get; set; }
    public Dictionary<string, object> LegacyProbe { get; set; } = [];
}
