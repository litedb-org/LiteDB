using System.Collections.Generic;

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
