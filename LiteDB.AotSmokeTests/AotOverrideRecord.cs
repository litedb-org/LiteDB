#nullable enable
namespace LiteDB.AotSmokeTests;

[BsonSourceGenerated]
public sealed class AotOverrideRecord : AotOverrideMiddle
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
