using System;
using LiteDB;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2610_Tests
{
    private class DateHolder
    {
        public DateTime Timestamp;
    }

    private static BsonDocument DateHolderSerializer(DateHolder dh)
    {
        var doc = new BsonDocument
        {
            ["holder"] = dh.Timestamp
        };
        return doc;
    }

    private static DateHolder DateHolderDeserializer(BsonDocument doc)
    {
        return new DateHolder
        {
            Timestamp = doc["holder"].AsDateTime
        };
    }

    [Fact]
    public void DateTime_Should_Roundtrip_With_BsonMapper()
    {
        var mapper = new BsonMapper();
        mapper.RegisterType<DateHolder>(
            serialize: dh => DateHolderSerializer(dh),
            deserialize: bv => DateHolderDeserializer((BsonDocument)bv));

        var original = new DateHolder { Timestamp = DateTime.UtcNow };

        var doc = mapper.Serialize(original);
        var extracted = mapper.Deserialize<DateHolder>(doc);

        Assert.Equal(original.Timestamp, extracted.Timestamp);
    }
}
