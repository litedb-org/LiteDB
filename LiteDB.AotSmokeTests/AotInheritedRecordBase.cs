using System.Collections.Generic;

#nullable enable
namespace LiteDB.AotSmokeTests;

public abstract class AotInheritedRecordBase
{
    [BsonId(false)]
    public int BaseId { get; set; }

    [BsonField("base_name")]
    public string BaseName { get; set; } = string.Empty;

    public List<string> BaseTags { get; set; } = [];

    [BsonIgnore]
    public string? IgnoredBaseValue { get; set; }
}

public class AotPerson
{
    public int AotPersonId { get; set; }
}

[BsonSourceGenerated]
public sealed class AotEmployee : AotPerson
{
    public string Name { get; set; } = string.Empty;
}
