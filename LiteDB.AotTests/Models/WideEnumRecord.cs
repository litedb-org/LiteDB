#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class WideEnumRecord
{
    public int Id { get; set; }
    public SignedWideState Signed { get; set; }
    public UnsignedWideState Unsigned { get; set; }
    public SignedWideState? NullableSigned { get; set; }
}
