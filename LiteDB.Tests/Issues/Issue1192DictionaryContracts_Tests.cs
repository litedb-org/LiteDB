using System;
using System.Collections;
using System.Collections.Generic;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1192DictionaryContracts_Tests
    {
        public class AmbiguousDictionary : Dictionary<string, int>, IDictionary<Guid, string>
        {
            private readonly IDictionary<Guid, string> _other = new Dictionary<Guid, string>();
            string IDictionary<Guid, string>.this[Guid key] { get => _other[key]; set => _other[key] = value; }
            ICollection<Guid> IDictionary<Guid, string>.Keys => _other.Keys;
            ICollection<string> IDictionary<Guid, string>.Values => _other.Values;
            int ICollection<KeyValuePair<Guid, string>>.Count => _other.Count;
            bool ICollection<KeyValuePair<Guid, string>>.IsReadOnly => false;
            void IDictionary<Guid, string>.Add(Guid key, string value) => _other.Add(key, value);
            bool IDictionary<Guid, string>.ContainsKey(Guid key) => _other.ContainsKey(key);
            bool IDictionary<Guid, string>.Remove(Guid key) => _other.Remove(key);
            bool IDictionary<Guid, string>.TryGetValue(Guid key, out string value) => _other.TryGetValue(key, out value);
            void ICollection<KeyValuePair<Guid, string>>.Add(KeyValuePair<Guid, string> item) => _other.Add(item);
            void ICollection<KeyValuePair<Guid, string>>.Clear() => _other.Clear();
            bool ICollection<KeyValuePair<Guid, string>>.Contains(KeyValuePair<Guid, string> item) => _other.Contains(item);
            void ICollection<KeyValuePair<Guid, string>>.CopyTo(KeyValuePair<Guid, string>[] array, int index) => _other.CopyTo(array, index);
            bool ICollection<KeyValuePair<Guid, string>>.Remove(KeyValuePair<Guid, string> item) => _other.Remove(item);
            IEnumerator<KeyValuePair<Guid, string>> IEnumerable<KeyValuePair<Guid, string>>.GetEnumerator() => _other.GetEnumerator();
        }

        public class HybridDictionary : Hashtable, IDictionary<Guid, string>
        {
            private readonly IDictionary<Guid, string> _other = new Dictionary<Guid, string>();
            string IDictionary<Guid, string>.this[Guid key] { get => _other[key]; set => _other[key] = value; }
            ICollection<Guid> IDictionary<Guid, string>.Keys => _other.Keys;
            ICollection<string> IDictionary<Guid, string>.Values => _other.Values;
            int ICollection<KeyValuePair<Guid, string>>.Count => _other.Count;
            bool ICollection<KeyValuePair<Guid, string>>.IsReadOnly => false;
            void IDictionary<Guid, string>.Add(Guid key, string value) => _other.Add(key, value);
            bool IDictionary<Guid, string>.ContainsKey(Guid key) => _other.ContainsKey(key);
            bool IDictionary<Guid, string>.Remove(Guid key) => _other.Remove(key);
            bool IDictionary<Guid, string>.TryGetValue(Guid key, out string value) => _other.TryGetValue(key, out value);
            void ICollection<KeyValuePair<Guid, string>>.Add(KeyValuePair<Guid, string> item) => _other.Add(item);
            void ICollection<KeyValuePair<Guid, string>>.Clear() => _other.Clear();
            bool ICollection<KeyValuePair<Guid, string>>.Contains(KeyValuePair<Guid, string> item) => _other.Contains(item);
            void ICollection<KeyValuePair<Guid, string>>.CopyTo(KeyValuePair<Guid, string>[] array, int index) => _other.CopyTo(array, index);
            bool ICollection<KeyValuePair<Guid, string>>.Remove(KeyValuePair<Guid, string> item) => _other.Remove(item);
            IEnumerator<KeyValuePair<Guid, string>> IEnumerable<KeyValuePair<Guid, string>>.GetEnumerator() => _other.GetEnumerator();
        }

        public class DirectHybridDictionary : HybridDictionary, IDictionary
        {
            object IDictionary.this[object key] { get => base[key]; set => base[key] = value; }
        }

        public class ReadOnlySideDictionary : Dictionary<string, int>, IReadOnlyDictionary<Guid, string>
        {
            string IReadOnlyDictionary<Guid, string>.this[Guid key] => "side";
            IEnumerable<Guid> IReadOnlyDictionary<Guid, string>.Keys => new[] { Guid.Empty };
            IEnumerable<string> IReadOnlyDictionary<Guid, string>.Values => new[] { "side" };
            int IReadOnlyCollection<KeyValuePair<Guid, string>>.Count => 1;
            bool IReadOnlyDictionary<Guid, string>.ContainsKey(Guid key) => key == Guid.Empty;
            bool IReadOnlyDictionary<Guid, string>.TryGetValue(Guid key, out string value) { value = "side"; return key == Guid.Empty; }
            IEnumerator<KeyValuePair<Guid, string>> IEnumerable<KeyValuePair<Guid, string>>.GetEnumerator()
                => ((IEnumerable<KeyValuePair<Guid, string>>)new[] { new KeyValuePair<Guid, string>(Guid.Empty, "side") }).GetEnumerator();
        }

        public class TaggedLegacy<TKey, TValue> : Hashtable { }
        public class TaggedLegacy<T> : Hashtable { }
        public class TaggedDictionary<T> : Dictionary<string, int> { }
        public class ReorderedDictionary<TValue, TKey> : Dictionary<TKey, TValue> { }
        public class ConcreteDictionary : Dictionary<string, Uri> { }

        [Fact]
        public void Inherited_legacy_dictionary_implementation_selects_its_own_contract()
        {
            var mapper = new BsonMapper();
            var source = new AmbiguousDictionary { ["number"] = 42 };
            Assert.Equal(42, mapper.Serialize(source).AsDocument["number"].AsInt32);
            Assert.Equal(42, mapper.Deserialize<AmbiguousDictionary>(new BsonDocument { ["number"] = 42 })["number"]);

            IDictionary<string, int> primary = source;
            IDictionary<Guid, string> alternate = source;
            alternate.Add(Guid.Empty, "chosen");
            Assert.Equal(42, mapper.Serialize(primary).AsDocument["number"].AsInt32);
            Assert.Equal(42, mapper.Serialize(alternate).AsDocument["number"].AsInt32);
        }

        [Fact]
        public void Custom_instantiator_cannot_select_an_ambiguous_dictionary_view()
        {
            var target = new AmbiguousDictionary();
            var mapper = new BsonMapper(type => target);
            Assert.ThrowsAny<Exception>(() => mapper.Deserialize<IDictionary<Guid, string>>(
                new BsonDocument { [Guid.Empty.ToString()] = "chosen" }));
            Assert.Empty((IDictionary<string, int>)target);
            Assert.Empty((IDictionary<Guid, string>)target);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Generic_side_store_does_not_change_legacy_mapping(bool directImplementation)
        {
            HybridDictionary source = directImplementation ? new DirectHybridDictionary() : new HybridDictionary();
            source["number"] = 42;
            var generic = (IDictionary<Guid, string>)source;
            generic.Add(Guid.Empty, "separate");
            var mapper = new BsonMapper();
            var document = mapper.Serialize(source.GetType(), source).AsDocument;
            Assert.Equal(42, document["number"].AsInt32);
            Assert.Single(document.GetElements());
            // Opaque custom adapters retain the historical declared mapping.
            Assert.Equal(42, mapper.Serialize(generic).AsDocument["number"].AsInt32);

            var result = (HybridDictionary)mapper.Deserialize(source.GetType(), document);
            Assert.Equal(42, result["number"]);
            Assert.Empty((IDictionary<Guid, string>)result);
        }

        [Fact]
        public void Read_only_dictionary_interface_retains_its_declared_schema()
        {
            var mapper = new BsonMapper();
            IReadOnlyDictionary<Guid, string> alternate = new ReadOnlySideDictionary { ["number"] = 42 };
            Assert.Equal(42, mapper.Serialize(alternate).AsDocument["number"].AsInt32);
            IReadOnlyDictionary<Guid, string> aligned = new Dictionary<Guid, string> { [Guid.Empty] = "kept" };
            Assert.Equal("kept", mapper.Serialize(aligned).AsDocument[Guid.Empty.ToString()].AsString);
        }

        [Fact]
        public void Opaque_two_argument_legacy_dictionary_preserves_its_historical_schema()
        {
            var mapper = new BsonMapper();
            var address = new Uri("https://example.test/opaque");
            var source = new TaggedLegacy<Guid, Uri> { [Guid.Empty] = address };
            var result = mapper.Deserialize<TaggedLegacy<Guid, Uri>>(mapper.Serialize(source));
            Assert.Equal(address, result[Guid.Empty]);
        }

        [Fact]
        public void Discriminator_cannot_replace_the_requested_dictionary_view()
        {
            var mapper = new BsonMapper();
            Assert.Throws<FormatException>(() => mapper.Deserialize<IDictionary<Guid, string>>(new BsonDocument
            {
                ["_type"] = typeof(HybridDictionary).AssemblyQualifiedName,
                [Guid.Empty.ToString()] = "chosen"
            }));
        }

        [Fact]
        public void Generic_legacy_dictionary_round_trips_without_using_its_tag_as_a_key_type()
        {
            var mapper = new BsonMapper();
            var source = new TaggedLegacy<Guid> { ["number"] = 42, ["name"] = "kept" };
            var result = mapper.Deserialize<TaggedLegacy<Guid>>(mapper.Serialize(source));
            Assert.Equal(42, result["number"]);
            Assert.Equal("kept", result["name"]);
        }

        [Fact]
        public void Inherited_dictionary_contract_wins_over_unrelated_generic_arguments()
        {
            var mapper = new BsonMapper();
            var source = new TaggedDictionary<Guid> { ["number"] = 42 };
            var result = mapper.Deserialize<TaggedDictionary<Guid>>(mapper.Serialize(source));
            Assert.Equal(42, result["number"]);
        }

        [Fact]
        public void Dictionary_generic_arguments_can_have_a_different_order_than_the_contract()
        {
            var mapper = new BsonMapper();
            var source = new ReorderedDictionary<int, Guid> { [Guid.Empty] = 42 };
            var result = mapper.Deserialize<ReorderedDictionary<int, Guid>>(mapper.Serialize(source));
            Assert.Equal(42, result[Guid.Empty]);
        }

        [Fact]
        public void Non_generic_subclass_and_erased_dictionary_keep_the_runtime_value_type()
        {
            var mapper = new BsonMapper();
            var address = new Uri("https://example.test/item");
            IDictionary source = new ConcreteDictionary { ["address"] = address };
            var result = mapper.Deserialize<ConcreteDictionary>(mapper.Serialize(source));
            Assert.Equal(address, result["address"]);
        }

        [Fact]
        public void Declared_dictionary_interface_round_trips()
        {
            var mapper = new BsonMapper();
            IDictionary<Guid, int> source = new Dictionary<Guid, int> { [Guid.Empty] = 42 };
            var result = mapper.Deserialize<IDictionary<Guid, int>>(mapper.Serialize(source));
            Assert.Equal(42, result[Guid.Empty]);
        }
    }
}
