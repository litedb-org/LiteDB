using System.Collections.Generic;

#nullable enable
namespace LiteDB.AotSmokeTests;

public abstract class AotMultiLevelInheritedParent : AotMultiLevelInheritedGrandparent
{
    public string ParentName { get; set; } = string.Empty;
    public List<string> Values { get; set; } = [];

    [BsonIgnore]
    public string? IgnoredParentValue { get; set; }
}
