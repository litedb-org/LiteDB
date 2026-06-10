using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues;

/// <summary>
/// Repro for the family of bugs caused by <c>new BsonValue(object)</c> building a
/// plain <see cref="BsonValue"/> (Type=Array/Document) instead of a
/// <see cref="BsonArray"/>/<see cref="BsonDocument"/> instance:
///   #2456 - new BsonValue(new string[]{...}).AsArray returns null
///   #2589 - .ToString() on a BsonValue built from a string[] throws NullReferenceException
///   #1952 - NullReferenceException when a string[] BsonValue is upserted/serialized
/// </summary>
public class Issue2456_Tests
{
    private static readonly string[] Items = { "a", "b", "c" };

    [Fact]
    public void BsonValue_From_StringArray_Exposes_AsArray()
    {
        var value = new BsonValue(Items);

        value.Type.Should().Be(BsonType.Array);
        value.IsArray.Should().BeTrue();

        var array = value.AsArray;
        Assert.NotNull(array);
        array.Count.Should().Be(3);
        array[0].AsString.Should().Be("a");
        array[1].AsString.Should().Be("b");
        array[2].AsString.Should().Be("c");
    }

    [Fact]
    public void BsonValue_From_StringArray_ToString_Returns_Json()
    {
        var value = new BsonValue(Items);

        value.ToString().Should().Be("[\"a\",\"b\",\"c\"]");
    }

    [Fact]
    public void BsonValue_From_Dictionary_Exposes_AsDocument()
    {
        var value = new BsonValue(new Dictionary<string, object>
        {
            ["x"] = 1,
            ["y"] = "two"
        });

        value.Type.Should().Be(BsonType.Document);

        var doc = value.AsDocument;
        Assert.NotNull(doc);
        doc["x"].AsInt32.Should().Be(1);
        doc["y"].AsString.Should().Be("two");
    }

    [Fact]
    public void Upsert_Document_With_StringArray_BsonValue_RoundTrips()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("docs");

        var doc = new BsonDocument
        {
            ["_id"] = 1,
            ["tags"] = new BsonValue(Items)
        };

        col.Upsert(doc).Should().BeTrue();

        var loaded = col.FindById(1);
        loaded["tags"].IsArray.Should().BeTrue();
        loaded["tags"].AsArray.Count.Should().Be(3);
        loaded["tags"].AsArray[2].AsString.Should().Be("c");
    }
}
