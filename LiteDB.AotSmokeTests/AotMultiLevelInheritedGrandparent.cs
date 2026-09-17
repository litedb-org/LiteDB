namespace LiteDB.AotSmokeTests;

public abstract class AotMultiLevelInheritedGrandparent
{
    [BsonId(false)]
    public int RootId { get; set; }

    [BsonField("origin")]
    public string Origin { get; set; } = string.Empty;
}
