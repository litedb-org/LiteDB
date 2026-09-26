using System.Collections.Generic;

#nullable enable
namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed class AotDynamicDictionaryRecord
{
    public int Id { get; set; }
    public Dictionary<string, object?> Fields { get; set; } = [];
}
