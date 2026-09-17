using LiteDB;

namespace LiteDB.SourceGenerator.PackageConsumer;

[BsonSourceGenerated]
public sealed class PackagedOverrideRecord : PackagedOverrideMiddle
{
    private int _overrideId;

    public override int OverrideId
    {
        get => _overrideId;
        set
        {
            _overrideId = value;
            SetterCalls++;
        }
    }

    public string Name { get; set; } = string.Empty;

    [BsonIgnore]
    public int SetterCalls { get; private set; }
}
