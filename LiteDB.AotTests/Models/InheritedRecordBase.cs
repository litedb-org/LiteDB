using System.Collections.Generic;

#nullable enable
namespace LiteDB.AotTests;

public abstract class InheritedRecordBase
{
    [BsonId(false)]
    public int BaseId { get; set; }

    [BsonField("base_name")]
    public string BaseName { get; set; } = string.Empty;

    public List<string> BaseTags { get; set; } = [];

    [BsonIgnore]
    public string? IgnoredBaseValue { get; set; }
}
