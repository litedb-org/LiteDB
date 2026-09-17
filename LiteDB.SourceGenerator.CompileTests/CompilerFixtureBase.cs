namespace LiteDB.SourceGenerator.CompileTests;

public abstract class CompilerFixtureBase
{
    [BsonId]
    public int Id { get; set; }

    [BsonField("base_name")]
    public string BaseName { get; set; } = string.Empty;
}
