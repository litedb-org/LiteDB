using System;
using System.Collections.Generic;

using LiteDB;

namespace LiteDB.SourceGenerator.PackageConsumer;

[BsonSourceGenerated]
public sealed class PackagedGeneratedRecord
{
    public int Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public int? Attempt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public Dictionary<string, object?> Fields { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string[] StreamNames { get; set; } = [];
}
