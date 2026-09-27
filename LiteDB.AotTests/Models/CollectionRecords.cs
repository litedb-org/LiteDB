using System.Collections.Generic;

#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class ComputedProjectionRecord
{
    public int Id { get; set; }
    public string NodeType { get; set; } = string.Empty;
    public List<string> Values { get; set; } = [];
    public string Fingerprint => string.Join("|", NodeType, string.Join("|", Values));
    public int ValueCount => Values.Count;
    public string ValueSummary => string.Join(",", Values);
}

[BsonSourceGenerated]
public sealed class DynamicDictionaryRecord
{
    public int Id { get; set; }
    public Dictionary<string, object?> Fields { get; set; } = [];
}

[BsonSourceGenerated]
public sealed class NullableListRecord
{
    public int Id { get; set; }
    public List<string>? Values { get; set; }
}

[BsonSourceGenerated]
public sealed class StringArrayRecord
{
    public int Id { get; set; }
    public string[]? StreamNames { get; set; }
}

internal sealed class UnsupportedDynamicDictionaryValue
{
}
