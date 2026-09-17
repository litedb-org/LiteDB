#nullable enable
namespace LiteDB.AotTests;

public abstract class HierarchyExecutionMiddle : HierarchyExecutionBase
{
    public override int EntityKey { get; set; }

    [BsonField("middle_name")]
    public override string DisplayName { get; set; } = string.Empty;

    public override string? IgnoredValue { get; set; }
}
