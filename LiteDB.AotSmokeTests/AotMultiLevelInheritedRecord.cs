namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed class AotMultiLevelInheritedRecord : AotMultiLevelInheritedParent
{
    public string DerivedName { get; set; } = string.Empty;
    public string Fingerprint => string.Join("|", Origin, DerivedName, string.Join(",", Values));
    public int ValueCount => Values.Count;
}
