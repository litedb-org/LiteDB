#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class OverrideRecord : OverrideRecordMiddle
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

    public override string Name { get; set; } = string.Empty;
    public override string? Ignored { get; set; }

    [BsonIgnore]
    public int SetterCalls { get; private set; }
}
