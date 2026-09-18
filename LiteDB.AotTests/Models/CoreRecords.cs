#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class ConventionIdRecord
{
    public int ConventionIdRecordId { get; set; }

    [BsonField(Name = "stored_name")]
    public string Name { get; set; } = string.Empty;

    [BsonIgnore]
    public string? IgnoredText { get; set; }

    [BsonIgnore]
    public int IgnoredNumber { get; set; }
}

[BsonSourceGenerated]
public sealed class ExplicitIdRecord
{
    [BsonId(false)]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

[BsonSourceGenerated]
public sealed class GeneratedRecord
{
    public int Id { get; set; }

    [BsonField("name")]
    public string Name { get; set; } = string.Empty;

    public System.Collections.Generic.List<string> Values { get; set; } = [];

    [BsonIgnore]
    public string? Ignored { get; set; }
}

[BsonSourceGenerated]
public sealed record MutableGeneratedRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

[BsonSourceGenerated]
public sealed class NoIdRecord
{
    public string Name { get; set; } = string.Empty;
}

[BsonSourceGenerated]
public sealed class SecondaryRecord
{
    public int Id { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class UnsupportedExecutionRecord
{
    public int Id { get; set; }
}
