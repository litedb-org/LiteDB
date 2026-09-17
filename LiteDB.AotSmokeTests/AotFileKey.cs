#nullable enable
namespace LiteDB.AotSmokeTests;

/// <summary>
/// An ordinary application class used as file storage id. It deliberately has no [BsonSourceGenerated] and no
/// registered converter: nothing but the annotation on the file id type parameter keeps its members alive.
/// </summary>
public sealed class AotFileKey
{
    public int Tenant { get; set; }

    public string Name { get; set; } = string.Empty;
}
