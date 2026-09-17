#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class HierarchyExecutionRecord : HierarchyExecutionMiddle
{
    private int _entityKey;

    public override int EntityKey
    {
        get => _entityKey;
        set
        {
            _entityKey = value;
            IdSetterCalls++;
        }
    }

    public override string DisplayName { get; set; } = string.Empty;
    public override string? IgnoredValue { get; set; }
    public int DerivedScore { get; set; }

    [BsonIgnore]
    public string Projection => $"{DisplayName}:{DerivedScore}";

    [BsonIgnore]
    public int IdSetterCalls { get; private set; }
}
