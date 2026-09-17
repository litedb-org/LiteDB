#nullable enable
namespace LiteDB.AotTests;

public class OverrideRecordMiddle : OverrideRecordBase
{
    public override int OverrideId { get; set; }
    public override string Name { get; set; } = string.Empty;
    public override string? Ignored { get; set; }
}
