using System;
using System.Collections.Generic;
using LiteDB.Generated;

namespace LiteDB.SourceGenerator.CompileTests;

public abstract class CompilerFixtureBase
{
    [BsonId]
    public int Id { get; set; }

    [BsonField("base_name")]
    public string BaseName { get; set; } = string.Empty;
}

[BsonSourceGenerated]
public sealed class NullableDynamicDictionaryConsumer : CompilerFixtureBase
{
    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public int? RetryCount { get; set; }

    public List<string> Tags { get; set; } = [];

    public string[] StreamNames { get; set; } = [];

    public Dictionary<string, object?> Fields { get; set; } = [];

    public string Fingerprint => string.Concat(BaseName, "|", RetryCount);
}

public static class GeneratedRegistrationConsumer
{
    public static void Register(BsonMapper mapper)
    {
        LiteDbGeneratedMappings.Register(mapper);
    }
}
