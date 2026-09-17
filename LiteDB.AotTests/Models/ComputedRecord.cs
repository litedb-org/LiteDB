namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class ComputedRecord
{
    public int Id { get; set; }
    public string NodeType { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string Fingerprint => string.Join("|", NodeType, ContentHash);
}
