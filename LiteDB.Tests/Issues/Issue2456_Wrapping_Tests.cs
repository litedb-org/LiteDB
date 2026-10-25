using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues;

/// <summary>
/// <c>new BsonValue(object)</c> must turn every CLR collection shape into a usable
/// BSON array or document, and must leave non-collection types alone.
/// </summary>
public class Issue2456_Wrapping_Tests
{
    public static IEnumerable<object[]> ArrayInputs()
    {
        yield return new object[] { "string[]", new[] { "a", "b" }, "[\"a\",\"b\"]" };
        yield return new object[] { "int[]", new[] { 1, 2 }, "[1,2]" };
        yield return new object[] { "long[]", new[] { 1L }, "[{\"$numberLong\":\"1\"}]" };
        yield return new object[] { "object[]", new object[] { 1, "a", null, true }, "[1,\"a\",null,true]" };
        yield return new object[] { "List<int>", new List<int> { 3 }, "[3]" };
        yield return new object[] { "List<object>", new List<object> { 1.5, "x" }, "[1.5,\"x\"]" };
        yield return new object[] { "HashSet<int>", new HashSet<int> { 7 }, "[7]" };
        yield return new object[] { "Queue<string>", new Queue<string>(new[] { "q" }), "[\"q\"]" };
        yield return new object[] { "IEnumerable<int> iterator", Iterate(4, 5), "[4,5]" };
        yield return new object[] { "BsonValue[]", new BsonValue[] { 1, "a" }, "[1,\"a\"]" };
        yield return new object[] { "List<BsonValue>", new List<BsonValue> { 1 }, "[1]" };
        yield return new object[] { "ReadOnlyCollection<BsonValue>", new ReadOnlyCollection<BsonValue>(new List<BsonValue> { 2 }), "[2]" };
        yield return new object[] { "BsonArray", new BsonArray { 8 }, "[8]" };
        yield return new object[] { "int[][]", new[] { new[] { 1 }, new[] { 2, 3 } }, "[[1],[2,3]]" };
        yield return new object[] { "List<BsonDocument>", new List<BsonDocument> { new BsonDocument { ["a"] = 1 } }, "[{\"a\":1}]" };
        yield return new object[] { "empty int[]", new int[0], "[]" };
        yield return new object[] { "empty List<object>", new List<object>(), "[]" };
    }

    public static IEnumerable<object[]> DocumentInputs()
    {
        yield return new object[] { "Dictionary<string,object>", new Dictionary<string, object> { ["x"] = 1, ["y"] = "two" }, "{\"x\":1,\"y\":\"two\"}" };
        yield return new object[] { "Dictionary<string,int>", new Dictionary<string, int> { ["n"] = 5 }, "{\"n\":5}" };
        yield return new object[] { "Dictionary<int,string>", new Dictionary<int, string> { [1] = "one" }, "{\"1\":\"one\"}" };
        yield return new object[] { "Dictionary<string,BsonValue>", new Dictionary<string, BsonValue> { ["b"] = true }, "{\"b\":true}" };
        yield return new object[] { "SortedDictionary<string,int>", new SortedDictionary<string, int> { ["z"] = 1, ["a"] = 2 }, "{\"a\":2,\"z\":1}" };
        yield return new object[] { "ConcurrentDictionary<string,BsonValue>", new ConcurrentDictionary<string, BsonValue>(new Dictionary<string, BsonValue> { ["c"] = 1 }), "{\"c\":1}" };
        yield return new object[] { "Hashtable", new Hashtable { ["h"] = 1 }, "{\"h\":1}" };
        yield return new object[] { "ReadOnlyDictionary", new ReadOnlyDictionary<string, BsonValue>(new Dictionary<string, BsonValue> { ["r"] = 1 }), "{\"r\":1}" };
        yield return new object[] { "BsonDocument", new BsonDocument { ["d"] = 1 }, "{\"d\":1}" };
        yield return new object[] { "nested", new Dictionary<string, object> { ["list"] = new List<object> { 1, "a" }, ["doc"] = new Dictionary<string, object> { ["n"] = null } }, "{\"list\":[1,\"a\"],\"doc\":{\"n\":null}}" };
        yield return new object[] { "empty Dictionary", new Dictionary<string, object>(), "{}" };
    }

    [Theory]
    [MemberData(nameof(ArrayInputs))]
    public void Array_shaped_inputs_should_become_usable_arrays(string label, object input, string expectedJson)
    {
        var value = new BsonValue(input);

        value.Type.Should().Be(BsonType.Array, label);
        value.IsArray.Should().BeTrue(label);
        ((object)value.AsArray).Should().NotBeNull(label);
        ((object)value.AsDocument).Should().BeNull(label);
        value.ToString().Should().Be(expectedJson, label);
        JsonSerializer.Deserialize(value.ToString()).Should().Be(value, label);
        value.Should().Be(value.AsArray, label);
        value.Should().Be(new BsonArray(value.AsArray), label);
    }

    [Theory]
    [MemberData(nameof(DocumentInputs))]
    public void Document_shaped_inputs_should_become_usable_documents(string label, object input, string expectedJson)
    {
        var value = new BsonValue(input);

        value.Type.Should().Be(BsonType.Document, label);
        value.IsDocument.Should().BeTrue(label);
        ((object)value.AsDocument).Should().NotBeNull(label);
        ((object)value.AsArray).Should().BeNull(label);
        // key order of a copied Dictionary is not contractual, so compare parsed documents
        value.Should().Be(JsonSerializer.Deserialize(expectedJson), label);
        JsonSerializer.Deserialize(value.ToString()).Should().Be(value, label);
        value.Should().Be(value.AsDocument, label);
        value.Should().Be(new BsonDocument(value.AsDocument), label);
    }

    [Fact]
    public void Non_collection_inputs_must_keep_their_scalar_type()
    {
        new BsonValue((object)"text").Type.Should().Be(BsonType.String);
        new BsonValue((object)new byte[] { 1, 2 }).Type.Should().Be(BsonType.Binary);
        new BsonValue((object)new float[] { 1f, 2f }).Type.Should().Be(BsonType.Vector);
        new BsonValue((object)1).Type.Should().Be(BsonType.Int32);
        new BsonValue((object)1L).Type.Should().Be(BsonType.Int64);
        new BsonValue((object)1.0).Type.Should().Be(BsonType.Double);
        new BsonValue((object)1m).Type.Should().Be(BsonType.Decimal);
        new BsonValue((object)true).Type.Should().Be(BsonType.Boolean);
        new BsonValue((object)Guid.Empty).Type.Should().Be(BsonType.Guid);
        new BsonValue((object)ObjectId.Empty).Type.Should().Be(BsonType.ObjectId);
        new BsonValue((object)DateTime.UtcNow).Type.Should().Be(BsonType.DateTime);
        new BsonValue((object)null).Type.Should().Be(BsonType.Null);
    }

    [Fact]
    public void Unsupported_element_types_should_still_throw_InvalidCastException()
    {
        var exception = Record.Exception(() => new BsonValue(new object[] { new Uri("http://localhost") }));

        exception.Should().BeOfType<InvalidCastException>();
    }

    [Fact]
    public void Null_elements_and_values_should_become_bson_null()
    {
        var array = new BsonValue(new object[] { null });
        var typedArray = new BsonValue((object)new BsonValue[] { null });
        var document = new BsonValue(new Dictionary<string, object> { ["n"] = null });
        var typedDocument = new BsonValue(new Dictionary<string, BsonValue> { ["n"] = null });

        array.AsArray[0].Should().Be(BsonValue.Null);
        typedArray.AsArray[0].Should().Be(BsonValue.Null);
        document.AsDocument["n"].Should().Be(BsonValue.Null);
        typedDocument.AsDocument["n"].Should().Be(BsonValue.Null);
        array.AsArray[0].Should().NotBeNull();
        typedDocument.AsDocument["n"].Should().NotBeNull();
    }

    [Fact]
    public void Wrapped_BsonValue_elements_should_be_kept_by_reference()
    {
        var inner = new BsonArray { 1 };
        var innerDocument = new BsonDocument { ["a"] = 1 };
        var value = new BsonValue((object)new BsonValue[] { inner, innerDocument });

        value.AsArray[0].Should().BeSameAs(inner);
        value.AsArray[1].Should().BeSameAs(innerDocument);
    }

    [Fact]
    public void Wrapping_a_wrapper_should_expose_the_same_content()
    {
        var wrapper = new BsonValue(new[] { "a" });
        var documentWrapper = new BsonValue(new Dictionary<string, object> { ["k"] = 1 });

        var copy = new BsonValue((object)wrapper);
        var documentCopy = new BsonValue((object)documentWrapper);

        copy.IsArray.Should().BeTrue();
        ((object)copy.AsArray).Should().NotBeNull();
        copy.Should().Be(wrapper);
        documentCopy.IsDocument.Should().BeTrue();
        ((object)documentCopy.AsDocument).Should().NotBeNull();
        documentCopy.Should().Be(documentWrapper);
    }

    [Fact]
    public void Dictionary_keys_should_be_case_insensitive_like_BsonDocument()
    {
        var value = new BsonValue(new Dictionary<string, object> { ["Name"] = "x" });
        // ordinal order enumerates "A" before "a", so "a" is the last value seen
        var mixed = new BsonValue(new SortedDictionary<string, object>(StringComparer.Ordinal) { ["a"] = 1, ["A"] = 2 });

        value.AsDocument["name"].AsString.Should().Be("x");
        value.AsDocument["NAME"].AsString.Should().Be("x");
        value.AsDocument.ContainsKey("nAmE").Should().BeTrue();
        mixed.AsDocument.Count.Should().Be(1);
        mixed.AsDocument["A"].AsInt32.Should().Be(1);
    }

    [Fact]
    public void Source_collections_must_not_be_modified_by_wrapping_or_by_adapter_mutation()
    {
        var sourceList = new List<BsonValue> { 1, null };
        var sourceDictionary = new Dictionary<string, BsonValue> { ["a"] = 1 };
        var sourceArray = new[] { "x" };

        var list = new BsonValue((object)sourceList);
        var dictionary = new BsonValue((object)sourceDictionary);
        var array = new BsonValue(sourceArray);

        list.AsArray.Add(2);
        list.AsArray[0] = 5;
        dictionary.AsDocument["b"] = 2;
        array.AsArray.Clear();

        sourceList.Should().Equal(new BsonValue[] { 1, null });
        sourceDictionary.Keys.Should().Equal("a");
        sourceArray.Should().Equal("x");
    }

    [Fact]
    public void Wrapped_fixed_size_and_read_only_sources_should_support_every_mutation()
    {
        var array = new BsonValue((object)new BsonValue[] { 1 });
        var document = new BsonValue((object)new ReadOnlyDictionary<string, BsonValue>(new Dictionary<string, BsonValue> { ["a"] = 1 }));

        array.AsArray.AddRange(new BsonValue[] { 2, 3 });
        array.AsArray.AddRange(new List<BsonValue> { 4 });
        array.AsArray.Insert(0, 0);
        array.AsArray.Remove(3).Should().BeTrue();
        document.AsDocument.Add("b", 2);
        document.AsDocument.Remove("a").Should().BeTrue();
        document.AsDocument["C"] = 3;

        array.AsArray.Select(x => x.AsInt32).Should().Equal(0, 1, 2, 4);
        document.AsDocument.Keys.Should().BeEquivalentTo("b", "C");
        document.AsDocument["c"].AsInt32.Should().Be(3);
    }

    [Fact]
    public void RawValue_should_stay_usable_as_a_collection_interface()
    {
        var array = new BsonValue(new[] { "a", "b" });
        var document = new BsonValue(new Dictionary<string, object> { ["k"] = 1 });

        (array.RawValue as IList<BsonValue>).Should().NotBeNull().And.HaveCount(2);
        (array.RawValue as IEnumerable<BsonValue>).Should().Equal("a", "b");
        (document.RawValue as IDictionary<string, BsonValue>).Should().NotBeNull().And.ContainKey("k");
    }

    private static IEnumerable<int> Iterate(params int[] values)
    {
        foreach (var value in values) yield return value;
    }
}
