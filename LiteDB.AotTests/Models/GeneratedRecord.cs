using System.Collections.Generic;

#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class GeneratedRecord
{
    public int Id { get; set; }

    [BsonField("name")]
    public string Name { get; set; } = string.Empty;

    public List<string> Values { get; set; } = [];

    [BsonIgnore]
    public string? Ignored { get; set; }
}
