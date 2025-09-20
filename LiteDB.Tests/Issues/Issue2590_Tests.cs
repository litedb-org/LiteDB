using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2590_Tests
{
    [Fact]
    public void ObjectId_AsString_Should_Return_Hex()
    {
        var id = ObjectId.NewObjectId();
        var doc = new BsonDocument {{"_id", id}};

        doc["_id"].Invoking(x => _ = x.AsString)
            .Should().NotThrow();
        doc["_id"].AsString.Should().Be(id.ToString());
    }
}
