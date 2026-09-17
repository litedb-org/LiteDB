using System.Collections.Generic;

#nullable enable
namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed class AotListRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string>? Values { get; set; } = [];
}
