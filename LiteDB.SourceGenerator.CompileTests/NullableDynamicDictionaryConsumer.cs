using System.Collections.Generic;
using LiteDB.Generated;

namespace LiteDB.SourceGenerator.CompileTests;

[BsonSourceGenerated]
public sealed class NullableDynamicDictionaryConsumer
{
    public int Id { get; set; }

    public Dictionary<string, object?> Fields { get; set; } = [];
}

public static class GeneratedRegistrationConsumer
{
    public static void Register(BsonMapper mapper)
    {
        LiteDbGeneratedMappings.Register(mapper);
    }
}
