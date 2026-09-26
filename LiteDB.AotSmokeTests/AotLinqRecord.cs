using System;

#nullable enable
namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed class AotLinqRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public AotNativeScalarState State { get; set; }
    public DateTime CreatedAt { get; set; }
    public string[] Tags { get; set; } = [];
}
