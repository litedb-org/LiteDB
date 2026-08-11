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
    public void BsonValue_From_StringArray_AsArray_Mutations_Update_Value()
    {
        var value = new BsonValue(Items);

        var array = value.AsArray;
        array[1] = "changed";
        array.Add("d");

        Assert.Equal("changed", value.AsArray[1].AsString);
        Assert.Equal("d", value.AsArray[3].AsString);
        Assert.Equal("[\"a\",\"changed\",\"c\",\"d\"]", value.ToString());
    }

    [Fact]
    public void BsonValue_From_StringArray_Direct_Indexer_Updates_Value()
    {
        var value = new BsonValue(Items);

        value[1] = "changed";

        Assert.Equal("changed", value[1].AsString);
        Assert.Equal("changed", value.AsArray[1].AsString);
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
    public void BsonValue_From_Dictionary_AsDocument_Mutations_Update_Value()
    {
        var value = new BsonValue(new Dictionary<string, object>
        {
            ["x"] = 1
        });

        var doc = value.AsDocument;
        doc["x"] = 2;
        doc["y"] = "two";

        Assert.Equal(2, value.AsDocument["x"].AsInt32);
        Assert.Equal("two", value.AsDocument["y"].AsString);
        Assert.Equal(2, value["x"].AsInt32);
        Assert.Equal("two", value["y"].AsString);
    }

    [Fact]
    public void BsonValue_From_Dictionary_Direct_Indexer_Updates_Value()
    {
        var value = new BsonValue(new Dictionary<string, object>
        {
            ["x"] = 1
        });

        value["x"] = 2;
        value["y"] = "two";

        Assert.Equal(2, value["x"].AsInt32);
        Assert.Equal("two", value.AsDocument["y"].AsString);
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

    [Fact]
    public void Wrapped_dictionary_must_keep_id_and_index_in_sync()
    {
        using var db = new LiteDatabase(new MemoryStream());
        var col = db.GetCollection("docs");
        var wrapped = new BsonValue(new Dictionary<string, BsonValue>
        {
            ["_id"] = 1
        });

        // The wrapped Dictionary uses a case-sensitive comparer. A regular
        // BsonDocument does not, so this spelling must update the same field.
        wrapped["_ID"] = 2;
        col.Insert(wrapped.AsDocument);

        var loaded = col.FindById(1);

        // The index says this is document 1. Its stored _id must agree.
        loaded["_id"].AsInt32.Should().Be(1);
    }

    [Fact]
    public void AsArray_should_return_one_stable_adapter()
    {
        var wrapped = new BsonValue(Items);

        // Serializers cache the byte length on BsonArray. Returning a fresh
        // adapter here discards that cache and makes nested arrays quadratic.
        Assert.Same(wrapped.AsArray, wrapped.AsArray);
    }

    [Fact]
    public void Null_inside_a_wrapped_array_should_serialize_as_Bson_null()
    {
        var wrapped = new BsonValue((object)new BsonValue[] { null });
        var document = new BsonDocument { ["value"] = wrapped };

        var exception = Record.Exception(() => BsonSerializer.Serialize(document));

        exception.Should().BeNull();
    }
}
