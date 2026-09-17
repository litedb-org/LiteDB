namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class ExplicitIdRecord
{
    [BsonId(false)]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
