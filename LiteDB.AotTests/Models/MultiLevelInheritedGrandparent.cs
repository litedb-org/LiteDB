namespace LiteDB.AotTests;

public abstract class MultiLevelInheritedGrandparent
{
    [BsonId(false)]
    public int RootId { get; set; }

    [BsonField("origin")]
    public string Origin { get; set; } = string.Empty;
}
