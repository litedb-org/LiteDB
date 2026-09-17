#nullable enable
namespace LiteDB.AotTests;

public abstract class HierarchyExecutionBase
{
    [BsonId(false)]
    public virtual int EntityKey { get; set; }

    [BsonField("base_name")]
    public virtual string DisplayName { get; set; } = string.Empty;

    [BsonIgnore]
    public virtual string? IgnoredValue { get; set; }
}
