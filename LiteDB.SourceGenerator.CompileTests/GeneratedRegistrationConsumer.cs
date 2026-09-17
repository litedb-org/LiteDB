using LiteDB.Generated;

namespace LiteDB.SourceGenerator.CompileTests;

public static class GeneratedRegistrationConsumer
{
    public static void Register(BsonMapper mapper)
    {
        LiteDbGeneratedMappings.Register(mapper);
    }
}
