namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed class AotInheritedRecord : AotInheritedRecordBase
{
    public string DerivedName { get; set; } = string.Empty;
    public string Fingerprint => string.Join("|", BaseName, DerivedName);
}
