using LiteDB;

namespace LiteDB.SourceGenerator.PackageConsumer;

public class PackagedOverrideBase
{
    [BsonId(false)]
    public virtual int OverrideId { get; set; }
}
