using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1903_Tests
    {
        public class Base { public string Name { get; set; } }
        public class Derived : Base
        {
            [BsonIgnore] public bool CustomDecoded { get; set; }
        }

        [Fact]
        public void Discriminator_selected_type_uses_registered_deserializer_and_its_result()
        {
            var calls = 0;
            var mapper = new BsonMapper();
            mapper.RegisterType<Derived>(x => new BsonDocument
            {
                ["_type"] = typeof(Derived).FullName + ", " + typeof(Derived).Assembly.GetName().Name,
                ["Name"] = x.Name
            }, x => { calls++; return new Derived { Name = x["Name"].AsString, CustomDecoded = true }; });
            var raw = new BsonDocument
            {
                ["_type"] = typeof(Derived).FullName + ", " + typeof(Derived).Assembly.GetName().Name,
                ["Name"] = "independently authored"
            };
            mapper.ToObject<Derived>(raw).CustomDecoded.Should().BeTrue();
            calls.Should().Be(1);
            var loaded = mapper.ToObject<Base>(raw);
            loaded.Should().BeOfType<Derived>().Which.CustomDecoded.Should().BeTrue();
            loaded.Name.Should().Be("independently authored");
            calls.Should().Be(2);
            using var db = new LiteDatabase(":memory:", mapper);
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["_type"] = raw["_type"], ["Name"] = "stored" });
            var stored = db.GetCollection<Base>("rows").FindById(1);
            stored.Should().BeOfType<Derived>().Which.CustomDecoded.Should().BeTrue();
            stored.Name.Should().Be("stored");
        }
    }
}
