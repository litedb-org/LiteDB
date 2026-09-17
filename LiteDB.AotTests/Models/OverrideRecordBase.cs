#nullable enable
namespace LiteDB.AotTests;

public class OverrideRecordBase
{
    [BsonId(false)]
    public virtual int OverrideId { get; set; }

    [BsonField("stored_name")]
    public virtual string Name { get; set; } = string.Empty;

    [BsonIgnore]
    public virtual string? Ignored { get; set; }
}
