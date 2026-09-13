using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class MapperInheritance_Tests
    {
        [Fact]
        public void Derived_mapping_applies_to_nested_objects_in_both_directions()
        {
            var mapper = new CustomMapper();
            var original = new Container { Entries = new[] { new Entry { Value = "hello" } } };

            var document = mapper.ToDocument(original);
            document["Entries"][0]["Value"].AsString.Should().Be("stored:hello");

            var restored = mapper.Deserialize<Container>(document);
            restored.Entries[0].Value.Should().Be("hello");
        }

        private sealed class CustomMapper : BsonMapper
        {
            protected override BsonDocument SerializeObject(Type type, object value, int depth)
            {
                var document = base.SerializeObject(type, value, depth);
                if (type == typeof(Entry)) document["Value"] = "stored:" + document["Value"].AsString;
                return document;
            }

            protected override void DeserializeObject(Type type, object value, BsonDocument document)
            {
                base.DeserializeObject(type, value, document);
                if (value is Entry entry) entry.Value = entry.Value.Substring("stored:".Length);
            }
        }

        public class Container
        {
            public Entry[] Entries { get; set; }
        }

        public class Entry
        {
            public string Value { get; set; }
        }
    }
}
