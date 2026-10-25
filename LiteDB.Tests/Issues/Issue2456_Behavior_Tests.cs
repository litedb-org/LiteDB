using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues;

/// <summary>
/// A wrapped collection must behave exactly like a native BsonArray/BsonDocument
/// for serialization, JSON, comparison, indexers and mutation.
/// </summary>
public class Issue2456_Behavior_Tests
{
    private static readonly string[] Tags = { "a", "b", "c" };

    private static BsonValue ArrayWrapper() => new BsonValue(Tags);

    // an ordered source keeps the copied key order deterministic for byte-level comparisons
    private static BsonValue DocumentWrapper() => new BsonValue(new SortedDictionary<string, object>(StringComparer.Ordinal) { ["x"] = 1, ["y"] = "two" });

    [Fact]
    public void Wrapped_values_should_serialize_to_identical_bytes_as_native_collections()
    {
        var wrapped = new BsonDocument
        {
            ["_id"] = 1,
            ["tags"] = ArrayWrapper(),
            ["meta"] = DocumentWrapper(),
            ["nested"] = new BsonValue(new object[] { new[] { 1, 2 }, new Dictionary<string, object> { ["k"] = null } })
        };
        var native = new BsonDocument
        {
            ["_id"] = 1,
            ["tags"] = new BsonArray { "a", "b", "c" },
            ["meta"] = new BsonDocument { ["x"] = 1, ["y"] = "two" },
            ["nested"] = new BsonArray { new BsonArray { 1, 2 }, new BsonDocument { ["k"] = BsonValue.Null } }
        };

        var wrappedBytes = BsonSerializer.Serialize(wrapped);
        var nativeBytes = BsonSerializer.Serialize(native);

        wrappedBytes.Should().Equal(nativeBytes);
        ((BsonValue)BsonSerializer.Deserialize(wrappedBytes)).Should().Be(native);
        BsonSerializer.Deserialize(wrappedBytes)["tags"].Should().BeOfType<BsonArray>();
        BsonSerializer.Deserialize(wrappedBytes)["meta"].Should().BeOfType<BsonDocument>();
    }

    [Fact]
    public void Serialization_should_see_mutations_made_after_a_previous_serialization()
    {
        var wrapper = ArrayWrapper();
        var document = new BsonDocument { ["_id"] = 1, ["tags"] = wrapper };

        var before = BsonSerializer.Serialize(document);
        wrapper.AsArray.Add("dddddddddd");
        wrapper.AsArray.RemoveAt(0);
        var after = BsonSerializer.Serialize(document);

        after.Length.Should().NotBe(before.Length);
        BsonSerializer.Deserialize(after)["tags"].AsArray.Select(x => x.AsString).Should().Equal("b", "c", "dddddddddd");
    }

    [Fact]
    public void Json_round_trip_should_preserve_wrapped_values()
    {
        var wrapper = new BsonValue(new Dictionary<string, object>
        {
            ["tags"] = Tags,
            ["count"] = 3,
            ["nested"] = new Dictionary<string, object> { ["ok"] = true }
        });

        var json = JsonSerializer.Serialize(wrapper);
        var pretty = JsonSerializer.Serialize(wrapper, indent: true);

        var parsed = JsonSerializer.Deserialize(json).AsDocument;
        parsed.Keys.Should().BeEquivalentTo("tags", "count", "nested");
        parsed["tags"].AsArray.Select(x => x.AsString).Should().Equal("a", "b", "c");
        parsed["count"].AsInt32.Should().Be(3);
        parsed["nested"]["ok"].AsBoolean.Should().BeTrue();
        JsonSerializer.Deserialize(json).Should().Be(wrapper);
        JsonSerializer.Deserialize(pretty).Should().Be(wrapper);
        wrapper.ToString().Should().Be(json);
    }

    [Fact]
    public void Wrapped_and_native_collections_should_compare_equal_in_both_directions()
    {
        var wrapper = ArrayWrapper();
        var native = new BsonArray { "a", "b", "c" };
        var documentWrapper = DocumentWrapper();
        var nativeDocument = new BsonDocument { ["y"] = "two", ["X"] = 1 };

        (wrapper == native).Should().BeTrue();
        (native == wrapper).Should().BeTrue();
        (wrapper != native).Should().BeFalse();
        wrapper.CompareTo(native).Should().Be(0);
        native.CompareTo(wrapper).Should().Be(0);
        (documentWrapper == nativeDocument).Should().BeTrue();
        (nativeDocument == documentWrapper).Should().BeTrue();
        documentWrapper.Equals((object)nativeDocument).Should().BeTrue();
        nativeDocument.Equals((object)documentWrapper).Should().BeTrue();
    }

    [Fact]
    public void Wrapped_collections_should_order_like_native_collections()
    {
        var smaller = new BsonValue(new[] { "a", "b" });
        var wrapper = ArrayWrapper();
        var larger = new BsonArray { "a", "b", "d" };

        (smaller < wrapper).Should().BeTrue();
        (wrapper < larger).Should().BeTrue();
        (larger > wrapper).Should().BeTrue();
        (wrapper >= smaller).Should().BeTrue();
        wrapper.CompareTo(BsonValue.Null).Should().BePositive();
        wrapper.CompareTo(1).Should().BePositive();
        wrapper.CompareTo("z").Should().BePositive();
        DocumentWrapper().CompareTo(wrapper).Should().BeNegative();
        wrapper.CompareTo(new byte[] { 1 }).Should().BeNegative();

        var values = new List<BsonValue> { larger, wrapper, smaller, DocumentWrapper(), 5, BsonValue.Null };
        values.Sort();
        values.Select(x => x.Type).Should().Equal(BsonType.Null, BsonType.Int32, BsonType.Document, BsonType.Array, BsonType.Array, BsonType.Array);
        values.Skip(3).Should().Equal(smaller, wrapper, larger);
    }

    [Fact]
    public void Indexers_on_wrapped_collections_should_behave_like_native_ones()
    {
        var array = ArrayWrapper();
        var document = DocumentWrapper();

        array[0].AsString.Should().Be("a");
        document["y"].AsString.Should().Be("two");
        document["Y"].AsString.Should().Be("two");
        document["missing"].Should().Be(BsonValue.Null);

        array[0] = null;
        document["x"] = null;
        array[0].Should().Be(BsonValue.Null);
        document["x"].Should().Be(BsonValue.Null);

        Record.Exception(() => array[5]).Should().BeOfType<ArgumentOutOfRangeException>();
        Record.Exception(() => array["k"]).Should().BeOfType<InvalidOperationException>();
        Record.Exception(() => array["k"] = 1).Should().BeOfType<InvalidOperationException>();
        Record.Exception(() => document[0]).Should().BeOfType<InvalidOperationException>();
        Record.Exception(() => document[0] = 1).Should().BeOfType<InvalidOperationException>();
        Record.Exception(() => new BsonValue(1)[0]).Should().BeOfType<InvalidOperationException>();
        Record.Exception(() => new BsonValue(1)["k"]).Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void Conversion_properties_should_return_stable_instances()
    {
        var array = ArrayWrapper();
        var document = DocumentWrapper();
        var nativeArray = new BsonArray { 1 };
        var nativeDocument = new BsonDocument { ["a"] = 1 };

        ((object)array.AsArray).Should().BeSameAs(array.AsArray);
        ((object)document.AsDocument).Should().BeSameAs(document.AsDocument);
        ((object)nativeArray.AsArray).Should().BeSameAs(nativeArray);
        ((object)nativeDocument.AsDocument).Should().BeSameAs(nativeDocument);
        ((object)new BsonValue(1).AsArray).Should().BeNull();
        ((object)new BsonValue(1).AsDocument).Should().BeNull();
        ((object)BsonValue.Null.AsArray).Should().BeNull();
    }

    [Fact]
    public void Adapter_mutations_should_be_visible_through_every_view()
    {
        var wrapper = ArrayWrapper();
        var adapter = wrapper.AsArray;

        adapter.Insert(0, "z");
        adapter.Remove("b");
        adapter.Add(new BsonValue(new[] { 1 }));
        wrapper.AsArray[3].AsArray.Add(2);

        wrapper.ToString().Should().Be("[\"z\",\"a\",\"c\",[1,2]]");
        wrapper.AsArray.Count.Should().Be(4);
        wrapper[3][1].AsInt32.Should().Be(2);
        wrapper.Should().Be(new BsonArray { "z", "a", "c", new BsonArray { 1, 2 } });

        adapter.Clear();
        wrapper.ToString().Should().Be("[]");
        wrapper.Should().Be(new BsonArray());
    }

    [Fact]
    public void Document_adapter_mutations_should_be_visible_through_every_view()
    {
        var wrapper = DocumentWrapper();
        var adapter = wrapper.AsDocument;

        adapter.Remove("x");
        adapter.Add("z", new BsonValue(new Dictionary<string, object> { ["n"] = 1 }));
        wrapper["z"]["n"] = 2;

        wrapper.AsDocument.Keys.Should().BeEquivalentTo("y", "z");
        wrapper["z"]["n"].AsInt32.Should().Be(2);
        JsonSerializer.Deserialize(wrapper.ToString()).Should().Be(wrapper);
        wrapper.Should().Be(new BsonDocument { ["y"] = "two", ["z"] = new BsonDocument { ["n"] = 2 } });
    }

    [Fact]
    public void Hash_codes_should_follow_content_and_mutation()
    {
        var wrapper = ArrayWrapper();
        var documentWrapper = DocumentWrapper();

        wrapper.GetHashCode().Should().Be(new BsonArray { "a", "b", "c" }.GetHashCode());
        documentWrapper.GetHashCode().Should().Be(new BsonDocument { ["Y"] = "two", ["X"] = 1L }.GetHashCode());
        new BsonValue(new int[0]).GetHashCode().Should().Be(new BsonArray().GetHashCode());
        new BsonValue(new Dictionary<string, object>()).GetHashCode().Should().Be(new BsonDocument().GetHashCode());

        wrapper.AsArray.Add("d");

        wrapper.GetHashCode().Should().Be(new BsonArray { "a", "b", "c", "d" }.GetHashCode());
        new HashSet<BsonValue> { wrapper }.Should().Contain((BsonValue)new BsonArray { "a", "b", "c", "d" });
    }

    [Fact]
    public void Deeply_nested_wrappers_should_serialize_compare_and_hash()
    {
        object payload = new[] { 1 };
        for (var i = 0; i < 200; i++) payload = new[] { payload };
        var wrapper = new BsonValue(payload);
        var clone = new BsonValue(payload);
        var document = new BsonDocument { ["_id"] = 1, ["deep"] = wrapper };

        wrapper.Should().Be(clone);
        wrapper.GetHashCode().Should().Be(clone.GetHashCode());
        ((BsonValue)BsonSerializer.Deserialize(BsonSerializer.Serialize(document))).Should().Be(document);
        JsonSerializer.Deserialize(wrapper.ToString()).Should().Be(wrapper);
    }

    [Fact]
    public void Large_wrapped_collections_should_serialize_compare_and_hash()
    {
        var numbers = Enumerable.Range(0, 100_000).ToArray();
        var wrapper = new BsonValue(numbers);
        var native = new BsonArray(numbers.Select(x => new BsonValue(x)));
        var pairs = numbers.Take(10_000).ToDictionary(x => "k" + x, x => (object)x);
        var documentWrapper = new BsonValue(pairs);

        wrapper.Should().Be(native);
        wrapper.GetHashCode().Should().Be(native.GetHashCode());
        documentWrapper.AsDocument.Count.Should().Be(10_000);
        documentWrapper.GetHashCode().Should().Be(new BsonValue(pairs).GetHashCode());

        var bytes = BsonSerializer.Serialize(new BsonDocument { ["_id"] = 1, ["n"] = wrapper, ["d"] = documentWrapper });
        var back = BsonSerializer.Deserialize(bytes);

        back["n"].AsArray.Count.Should().Be(100_000);
        back["d"].AsDocument["k9999"].AsInt32.Should().Be(9999);
    }

    [Fact]
    public void Type_flags_should_describe_the_wrapped_collection()
    {
        var array = ArrayWrapper();
        var document = DocumentWrapper();

        array.IsArray.Should().BeTrue();
        array.IsDocument.Should().BeFalse();
        array.IsNull.Should().BeFalse();
        array.IsString.Should().BeFalse();
        array.IsNumber.Should().BeFalse();
        document.IsDocument.Should().BeTrue();
        document.IsArray.Should().BeFalse();
        array.Should().NotBeOfType<BsonArray>();
        document.Should().NotBeOfType<BsonDocument>();
    }
}
