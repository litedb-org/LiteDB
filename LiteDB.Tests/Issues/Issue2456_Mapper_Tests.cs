using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues;

/// <summary>
/// BsonMapper must deserialize wrapped collections into CLR types and keep them
/// intact when they travel inside mapped entities.
/// </summary>
public class Issue2456_Mapper_Tests
{
    private static readonly string[] Tags = { "a", "b", "c" };

    private class Entity
    {
        public int Id { get; set; }
        public string[] Tags { get; set; }
        public List<int> Numbers { get; set; }
        public Dictionary<string, int> Counts { get; set; }
        public List<Child> Children { get; set; }
        public BsonValue Raw { get; set; }
        public BsonArray RawArray { get; set; }
        public BsonDocument RawDocument { get; set; }
    }

    private class Child
    {
        public string Name { get; set; }
    }

    private class Label
    {
        public string Text { get; set; }
    }

    private class Labelled
    {
        public int Id { get; set; }
        public Label[] Labels { get; set; }
    }

    [Fact]
    public void Deserialize_should_map_wrapped_arrays_to_clr_collections()
    {
        var mapper = new BsonMapper();
        var wrapper = new BsonValue(Tags);
        var numbers = new BsonValue(new[] { 1, 2, 3 });

        mapper.Deserialize<string[]>(wrapper).Should().Equal(Tags);
        mapper.Deserialize<List<string>>(wrapper).Should().Equal(Tags);
        mapper.Deserialize<IEnumerable<string>>(wrapper).Should().Equal(Tags);
        mapper.Deserialize<HashSet<int>>(numbers).Should().BeEquivalentTo(new[] { 1, 2, 3 });
        ((object)mapper.Deserialize<BsonArray>(wrapper)).Should().BeSameAs(wrapper.AsArray);
        mapper.Deserialize<BsonValue>(wrapper).Should().Be(wrapper);
    }

    [Fact]
    public void Deserialize_should_map_wrapped_documents_to_clr_types()
    {
        var mapper = new BsonMapper();
        var counts = new BsonValue(new Dictionary<string, object> { ["x"] = 1, ["y"] = 2 });
        var child = new BsonValue(new Dictionary<string, object> { ["Name"] = "kid" });

        mapper.Deserialize<Dictionary<string, int>>(counts).Should().Equal(new Dictionary<string, int> { ["x"] = 1, ["y"] = 2 });
        mapper.Deserialize<Child>(child).Name.Should().Be("kid");
        ((object)mapper.Deserialize<BsonDocument>(child)).Should().BeSameAs(child.AsDocument);
        mapper.ToObject<Child>(child.AsDocument).Name.Should().Be("kid");
    }

    [Fact]
    public void ToObject_should_read_entities_whose_fields_hold_wrapped_values()
    {
        var mapper = new BsonMapper();
        var document = new BsonDocument
        {
            ["_id"] = 1,
            ["Tags"] = new BsonValue(Tags),
            ["Numbers"] = new BsonValue(new List<object> { 1, 2 }),
            ["Counts"] = new BsonValue(new Dictionary<string, object> { ["a"] = 1 }),
            ["Children"] = new BsonValue(new object[] { new Dictionary<string, object> { ["Name"] = "one" }, new Dictionary<string, object> { ["Name"] = "two" } }),
            ["Raw"] = new BsonValue(new[] { 9 }),
            ["RawArray"] = new BsonValue(new[] { 8 }),
            ["RawDocument"] = new BsonValue(new Dictionary<string, object> { ["r"] = 7 })
        };

        var entity = mapper.ToObject<Entity>(document);

        entity.Id.Should().Be(1);
        entity.Tags.Should().Equal(Tags);
        entity.Numbers.Should().Equal(1, 2);
        entity.Counts.Should().Equal(new Dictionary<string, int> { ["a"] = 1 });
        entity.Children.Select(x => x.Name).Should().Equal("one", "two");
        entity.Raw.AsArray[0].AsInt32.Should().Be(9);
        entity.RawArray[0].AsInt32.Should().Be(8);
        entity.RawDocument["r"].AsInt32.Should().Be(7);
    }

    [Fact]
    public void ToDocument_should_keep_wrapped_values_on_bson_typed_members()
    {
        var mapper = new BsonMapper();
        var entity = new Entity
        {
            Id = 1,
            Raw = new BsonValue(Tags),
            RawArray = new BsonValue(new[] { 1 }).AsArray,
            RawDocument = new BsonValue(new Dictionary<string, object> { ["r"] = 7 }).AsDocument
        };

        var document = mapper.ToDocument(entity);
        var back = mapper.ToObject<Entity>(BsonSerializer.Deserialize(BsonSerializer.Serialize(document)));

        document["Raw"].AsArray.Select(x => x.AsString).Should().Equal(Tags);
        document["RawArray"].Should().BeSameAs(entity.RawArray);
        document["RawDocument"].Should().BeSameAs(entity.RawDocument);
        back.Raw.AsArray.Select(x => x.AsString).Should().Equal(Tags);
        back.RawArray[0].AsInt32.Should().Be(1);
        back.RawDocument["r"].AsInt32.Should().Be(7);
    }

    [Fact]
    public void Serialize_should_pass_wrappers_through_unchanged()
    {
        var mapper = new BsonMapper();
        var wrapper = new BsonValue(Tags);

        var serialized = mapper.Serialize(typeof(BsonValue), wrapper);
        var asObject = mapper.Serialize(typeof(object), wrapper);

        serialized.IsArray.Should().BeTrue();
        serialized.AsArray.Select(x => x.AsString).Should().Equal(Tags);
        asObject.IsArray.Should().BeTrue();
    }

    [Fact]
    public void Custom_type_registration_returning_wrapped_arrays_should_round_trip_through_the_database()
    {
        var mapper = new BsonMapper();
        mapper.RegisterType<Label[]>(
            serialize: labels => new BsonValue(labels.Select(x => x.Text).ToArray()),
            deserialize: value => value.AsArray.Select(x => new Label { Text = x.AsString }).ToArray());

        using var db = new LiteDatabase(new MemoryStream(), mapper);
        var col = db.GetCollection<Labelled>("labelled");
        var entity = new Labelled { Id = 1, Labels = new[] { new Label { Text = "x" }, new Label { Text = "y" } } };

        col.Upsert(entity).Should().BeTrue();
        col.Upsert(entity).Should().BeFalse();

        var loaded = col.FindById(1);
        var raw = db.GetCollection("labelled").FindById(1);

        loaded.Labels.Select(x => x.Text).Should().Equal("x", "y");
        raw["Labels"].Should().BeOfType<BsonArray>();
        raw["Labels"].AsArray.Select(x => x.AsString).Should().Equal("x", "y");
        col.Find(BsonExpression.Create("$.Labels ANY = @0", "y")).Should().ContainSingle();
    }
}
