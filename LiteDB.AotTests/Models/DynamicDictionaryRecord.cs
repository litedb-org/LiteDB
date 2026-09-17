using System.Collections.Generic;

#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class DynamicDictionaryRecord
{
    public int Id { get; set; }
    public Dictionary<string, object?> Fields { get; set; } = [];
}
