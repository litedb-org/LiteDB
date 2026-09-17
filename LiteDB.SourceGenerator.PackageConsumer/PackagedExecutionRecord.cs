using System;

using LiteDB;

namespace LiteDB.SourceGenerator.PackageConsumer;

[BsonSourceGenerated]
public sealed class PackagedExecutionRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Score { get; set; }

    // Keeps this retained manual Phase B package fixture outside C2 automatic scalar-map emission.
    public DateTimeOffset LegacyProbe { get; set; }
}
