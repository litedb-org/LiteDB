#nullable enable
namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed record AotMutableRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
