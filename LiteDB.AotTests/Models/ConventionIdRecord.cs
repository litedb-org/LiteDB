#nullable enable
namespace LiteDB.AotTests;

[BsonSourceGenerated]
public sealed class ConventionIdRecord
{
    public int ConventionIdRecordId { get; set; }

    [BsonField(Name = "stored_name")]
    public string Name { get; set; } = string.Empty;

    [BsonIgnore]
    public string? IgnoredText { get; set; }

    [BsonIgnore]
    public int IgnoredNumber { get; set; }
}
