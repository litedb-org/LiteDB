namespace LiteDB.AotSmokeTests;

public class AotOverrideBase
{
    [BsonId(false)]
    public virtual int OverrideId { get; set; }
}
