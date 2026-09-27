using System.Collections.Generic;
using LiteDB;

namespace LiteDB.SourceGenerator.PackageConsumer;

[BsonSourceGenerated]
public sealed class PackagedGeneratedRecord
{
    public ulong Unsigned { get; set; }
    public WideUnsigned State { get; set; }
    public Dictionary<string, object?> Fields { get; set; } = new();
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
