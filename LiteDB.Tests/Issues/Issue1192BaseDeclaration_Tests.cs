using System;
using System.Collections;
using System.Collections.Generic;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1192BaseDeclaration_Tests
    {
        public interface IPair<TKey, TValue> { }
        public class RuntimeMap : Dictionary<string, int>, IPair<Guid, Uri> { }
        public class OpaqueMap : Legacy<Uri, Guid> { }
        public interface IMarker : IDictionary { }
        public interface ITaggedMarker<T> : IDictionary { }
        public class MarkedMap<TKey, TValue> : Dictionary<TKey, TValue>, IMarker, ITaggedMarker<int> { }
        public class Base<TKey, TValue> { }
        public class Legacy<TValue, TKey> : Base<TKey, TValue>, IDictionary
        {
            private readonly Hashtable _items = new Hashtable();
            public object this[object key] { get => _items[key]; set => _items[key] = value; }
            public ICollection Keys => _items.Keys;
            public ICollection Values => _items.Values;
            public bool IsReadOnly => false;
            public bool IsFixedSize => false;
            public int Count => _items.Count;
            public object SyncRoot => _items.SyncRoot;
            public bool IsSynchronized => false;
            public void Add(object key, object value) => _items.Add(key, value);
            public void Clear() => _items.Clear();
            public bool Contains(object key) => _items.Contains(key);
            public void Remove(object key) => _items.Remove(key);
            public void CopyTo(Array array, int index) => _items.CopyTo(array, index);
            public IDictionaryEnumerator GetEnumerator() => _items.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
        }

        [Theory]
        [InlineData(typeof(IEnumerable))]
        [InlineData(typeof(IEnumerable<KeyValuePair<string, BsonValue>>))]
        public void Non_dictionary_interfaces_use_the_discriminator_dictionary_schema(Type declaration)
        {
            var document = new BsonDocument
            {
                ["_type"] = typeof(Dictionary<string, BsonValue>).AssemblyQualifiedName,
                ["value"] = 42
            };
            var result = Assert.IsType<Dictionary<string, BsonValue>>(new BsonMapper().Deserialize(declaration, document));
            Assert.Equal(42, result["value"].AsInt32);
        }

        [Theory]
        [InlineData(typeof(IMarker))]
        [InlineData(typeof(ITaggedMarker<int>))]
        public void Marker_interfaces_without_a_schema_use_the_discriminator_dictionary_schema(Type declaration)
        {
            var document = new BsonDocument
            {
                ["_type"] = typeof(MarkedMap<string, BsonValue>).AssemblyQualifiedName,
                ["value"] = 42
            };
            var result = Assert.IsType<MarkedMap<string, BsonValue>>(new BsonMapper().Deserialize(declaration, document));
            Assert.Equal(42, result["value"].AsInt32);
        }

        [Fact]
        public void Non_dictionary_interface_arguments_do_not_override_a_non_generic_factory_result()
        {
            var target = new RuntimeMap();
            var mapper = new BsonMapper(type => target);
            Assert.Same(target, mapper.Deserialize<IPair<Guid, Uri>>(new BsonDocument { ["answer"] = 42 }));
            Assert.Equal(42, target["answer"]);
        }

        [Fact]
        public void Non_generic_opaque_factory_result_preserves_its_historical_object_schema()
        {
            var target = new OpaqueMap();
            var mapper = new BsonMapper(type => target);
            Assert.Same(target, mapper.Deserialize<Base<Guid, Uri>>(new BsonDocument { ["answer"] = 42 }));
            Assert.Equal(42, target["answer"]);
        }

        [Fact]
        public void Generic_base_without_dictionary_interfaces_preserves_its_declared_schema()
        {
            var values = new[] { Issue1192DeclaredSchema_Tests.ByteEnum.First };
            Base<string, Issue1192DeclaredSchema_Tests.ByteEnum[]> source =
                new Legacy<Issue1192DeclaredSchema_Tests.ByteEnum[], string> { ["value"] = values };
            var target = new Legacy<Issue1192DeclaredSchema_Tests.ByteEnum[], string>();
            var mapper = new BsonMapper(type => target);
            var document = mapper.Serialize(source);
            Assert.True(document.AsDocument["value"].IsArray);
            var result = mapper.Deserialize<Base<string, Issue1192DeclaredSchema_Tests.ByteEnum[]>>(document);
            Assert.Same(target, result);
            Assert.Equal(values, Assert.IsType<Issue1192DeclaredSchema_Tests.ByteEnum[]>(target["value"]));
        }
    }
}
