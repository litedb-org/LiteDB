using LiteDB;

namespace LiteDB.SourceGenerator.PackageConsumer;

[BsonSourceGenerated]
public sealed record PackagedMutableRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
