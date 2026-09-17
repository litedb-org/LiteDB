#nullable enable
namespace LiteDB.AotTests;

public abstract class MultiLevelInheritedParent : MultiLevelInheritedGrandparent
{
    public string ParentName { get; set; } = string.Empty;

    [BsonIgnore]
    public string? IgnoredParentValue { get; set; }
}
