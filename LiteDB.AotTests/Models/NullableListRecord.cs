using System.Collections.Generic;

#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class NullableListRecord
{
    public int Id { get; set; }
    public List<string>? Values { get; set; }
}
