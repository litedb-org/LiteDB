using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1192DirectDictionary_Tests
    {
        public class DirectDictionary<TKey, TValue> : Hashtable, IDictionary<TKey, TValue>, IDictionary
        {
            object IDictionary.this[object key] { get => base[key]; set => base[key] = value; }
            TValue IDictionary<TKey, TValue>.this[TKey key] { get => (TValue)base[key]; set => base[key] = value; }
            ICollection<TKey> IDictionary<TKey, TValue>.Keys => Keys.Cast<TKey>().ToArray();
            ICollection<TValue> IDictionary<TKey, TValue>.Values => Values.Cast<TValue>().ToArray();
            bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;
            void IDictionary<TKey, TValue>.Add(TKey key, TValue value) => Add(key, value);
            bool IDictionary<TKey, TValue>.ContainsKey(TKey key) => ContainsKey(key);
            bool IDictionary<TKey, TValue>.Remove(TKey key)
            {
                var found = ContainsKey(key);
                Remove(key);
                return found;
            }
            bool IDictionary<TKey, TValue>.TryGetValue(TKey key, out TValue value)
            {
                var found = ContainsKey(key);
                value = found ? (TValue)base[key] : default(TValue);
                return found;
            }
            void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
            bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> item)
                => ContainsKey(item.Key) && EqualityComparer<TValue>.Default.Equals((TValue)base[item.Key], item.Value);
            void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
            {
                foreach (var item in (IEnumerable<KeyValuePair<TKey, TValue>>)this) array[index++] = item;
            }
            bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> item)
                => ((ICollection<KeyValuePair<TKey, TValue>>)this).Contains(item) && ((IDictionary<TKey, TValue>)this).Remove(item.Key);
            IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
            {
                foreach (TKey key in Keys) yield return new KeyValuePair<TKey, TValue>(key, (TValue)base[key]);
            }
        }

        public class TaggedDirect<TKey, TValue, TTag> : DirectDictionary<TKey, TValue>, IDictionary
        {
            object IDictionary.this[object key] { get => base[key]; set => base[key] = value; }
        }

        public class ReorderedDirect<TValue, TKey> : DirectDictionary<TKey, TValue>, IDictionary
        {
            object IDictionary.this[object key] { get => base[key]; set => base[key] = value; }
        }

        public class GenericSide<TTag> : Issue1192DictionaryContracts_Tests.HybridDictionary, IDictionary
        {
            object IDictionary.this[object key] { get => base[key]; set => base[key] = value; }
        }

        public class DirectMulti<TKey, TValue> : Issue1192DictionaryContracts_Tests.AmbiguousDictionary, IDictionary
        {
            object IDictionary.this[object key] { get => this[(string)key]; set => this[(string)key] = (int)value; }
        }

        public class LegacyReadOnly<TKey, TValue> : Hashtable, IReadOnlyDictionary<TKey, TValue>
        {
            TValue IReadOnlyDictionary<TKey, TValue>.this[TKey key] => (TValue)base[key];
            IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys.Cast<TKey>();
            IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values.Cast<TValue>();
            bool IReadOnlyDictionary<TKey, TValue>.ContainsKey(TKey key) => ContainsKey(key);
            bool IReadOnlyDictionary<TKey, TValue>.TryGetValue(TKey key, out TValue value)
            {
                var found = ContainsKey(key);
                value = found ? (TValue)base[key] : default(TValue);
                return found;
            }
            IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
            {
                foreach (TKey key in Keys) yield return new KeyValuePair<TKey, TValue>(key, (TValue)base[key]);
            }
        }

        public class FixedReadOnly : LegacyReadOnly<Guid, Uri> { }
        public class GenericSplit<TKey, TValue> : Issue1192DictionaryContracts_Tests.HybridDictionary { }

        public class MultipleSideStores<TKey, TValue> : Issue1192DictionaryContracts_Tests.HybridDictionary, IReadOnlyDictionary<long, bool>
        {
            bool IReadOnlyDictionary<long, bool>.this[long key] => true;
            IEnumerable<long> IReadOnlyDictionary<long, bool>.Keys => new[] { 1L };
            IEnumerable<bool> IReadOnlyDictionary<long, bool>.Values => new[] { true };
            int IReadOnlyCollection<KeyValuePair<long, bool>>.Count => 1;
            bool IReadOnlyDictionary<long, bool>.ContainsKey(long key) => key == 1;
            bool IReadOnlyDictionary<long, bool>.TryGetValue(long key, out bool value) { value = true; return key == 1; }
            IEnumerator<KeyValuePair<long, bool>> IEnumerable<KeyValuePair<long, bool>>.GetEnumerator()
                => ((IEnumerable<KeyValuePair<long, bool>>)new[] { new KeyValuePair<long, bool>(1, true) }).GetEnumerator();
        }

        public class DirectGenericSplit<TKey, TValue> : GenericSplit<TKey, TValue>, IDictionary
        {
            object IDictionary.this[object key] { get => base[key]; set => base[key] = value; }
        }

        [Fact]
        public void Custom_factory_preserves_the_constructed_dictionary_schema_for_a_legacy_base()
        {
            var target = new LegacyReadOnly<Guid, Uri>();
            var address = new Uri("https://example.test/factory");
            var result = new BsonMapper(type => target).Deserialize<Hashtable>(
                new BsonDocument { [Guid.Empty.ToString()] = address.AbsoluteUri });
            Assert.Equal(address, target[Guid.Empty]);
            Assert.Same(target, result);
        }

        [Fact]
        public void Generic_reimplementation_does_not_override_opaque_base_or_erased_declarations()
        {
            var value = new DirectGenericSplit<Guid, string> { ["number"] = 42 };
            var mapper = new BsonMapper();
            mapper.RegisterType<string>(text => text, text => text.AsString);
            Issue1192DictionaryContracts_Tests.HybridDictionary opaque = value;
            IDictionary erased = value;
            Assert.Equal(42, mapper.Serialize(opaque).AsDocument["number"].AsInt32);
            Assert.Equal(42, mapper.Serialize(erased).AsDocument["number"].AsInt32);
        }

        [Fact]
        public void Opaque_base_declaration_does_not_adopt_runtime_side_store_types()
        {
            Issue1192DictionaryContracts_Tests.HybridDictionary source = new GenericSplit<Guid, string> { ["number"] = 42 };
            var mapper = new BsonMapper();
            mapper.RegisterType<string>(value => value, value => value.AsString);
            Assert.Equal(42, mapper.Serialize(source).AsDocument["number"].AsInt32);
        }

        [Fact]
        public void Explicit_opaque_view_selects_its_schema_despite_other_side_contracts()
        {
            var source = new MultipleSideStores<object, object> { [Guid.Empty] = "kept" };
            var mapper = new BsonMapper();
            var document = mapper.Serialize((IDictionary<Guid, string>)source);
            Assert.Equal("kept", document.AsDocument[Guid.Empty.ToString()].AsString);
            var target = new MultipleSideStores<object, object>();
            new BsonMapper(type => target).Deserialize<IDictionary<Guid, string>>(document);
            Assert.Equal("kept", target[Guid.Empty]);
        }

        [Fact]
        public void Unrelated_multiple_side_contracts_do_not_make_the_legacy_store_ambiguous()
        {
            var mapper = new BsonMapper();
            var source = new MultipleSideStores<object, object> { ["number"] = 42 };
            var result = mapper.Deserialize<MultipleSideStores<object, object>>(mapper.Serialize(source));
            Assert.Equal(42, result["number"]);
        }

        [Fact]
        public void Erased_opaque_dictionary_does_not_take_a_generic_side_stores_schema()
        {
            IDictionary source = new GenericSplit<Guid, string> { ["number"] = 42 };
            var mapper = new BsonMapper();
            mapper.RegisterType<string>(value => value, value => value.AsString);
            Assert.Equal(42, mapper.Serialize(source).AsDocument["number"].AsInt32);
        }

        [Fact]
        public void Non_generic_read_only_dictionary_preserves_explicit_interface_mapping()
        {
            var address = new Uri("https://example.test/fixed");
            IReadOnlyDictionary<Guid, Uri> source = new FixedReadOnly { [Guid.Empty] = address };
            var document = new BsonMapper().Serialize(source);
            var target = new FixedReadOnly();
            new BsonMapper(type => target).Deserialize<IReadOnlyDictionary<Guid, Uri>>(document);
            Assert.Equal(address, target[Guid.Empty]);
        }

        [Fact]
        public void Direct_multiple_views_preserve_matching_first_two_arguments()
        {
            var mapper = new BsonMapper();
            var source = new DirectMulti<string, int> { ["number"] = 42 };
            ((IDictionary<Guid, string>)source).Add(Guid.Empty, "side");
            var result = mapper.Deserialize<DirectMulti<string, int>>(mapper.Serialize(source));
            Assert.Equal(42, result["number"]);
            Assert.Equal(0, ((ICollection<KeyValuePair<Guid, string>>)result).Count);
            Assert.Equal(42, mapper.Serialize((IDictionary<Guid, string>)source).AsDocument["number"].AsInt32);
        }

        [Fact]
        public void Read_only_contract_preserves_legacy_dictionary_key_and_value_types()
        {
            var mapper = new BsonMapper();
            var address = new Uri("https://example.test/readonly");
            var source = new LegacyReadOnly<Guid, Uri> { [Guid.Empty] = address };
            var result = mapper.Deserialize<LegacyReadOnly<Guid, Uri>>(mapper.Serialize(source));
            Assert.Equal(address, result[Guid.Empty]);
            var document = mapper.Serialize((IReadOnlyDictionary<Guid, Uri>)source);
            var target = new LegacyReadOnly<Guid, Uri>();
            new BsonMapper(type => target).Deserialize<IReadOnlyDictionary<Guid, Uri>>(document);
            Assert.Equal(address, target[Guid.Empty]);
        }

        [Fact]
        public void Direct_generic_implementation_preserves_its_matching_key_and_value_mapping()
        {
            var mapper = new BsonMapper();
            var address = new Uri("https://example.test/direct");
            var source = new DirectDictionary<Guid, Uri> { [Guid.Empty] = address };
            var result = mapper.Deserialize<DirectDictionary<Guid, Uri>>(mapper.Serialize(source));
            Assert.Equal(address, result[Guid.Empty]);
        }

        [Fact]
        public void Extra_tag_arguments_preserve_the_existing_first_two_type_mapping()
        {
            var mapper = new BsonMapper();
            var address = new Uri("https://example.test/tagged");
            var source = new TaggedDirect<Guid, Uri, int> { [Guid.Empty] = address };
            var result = mapper.Deserialize<TaggedDirect<Guid, Uri, int>>(mapper.Serialize(source));
            Assert.Equal(address, ((IDictionary<Guid, Uri>)result)[Guid.Empty]);
        }

        [Fact]
        public void Explicit_dictionary_interface_preserves_reordered_direct_implementation_mapping()
        {
            var mapper = new BsonMapper();
            var address = new Uri("https://example.test/reordered");
            IDictionary<Guid, Uri> source = new ReorderedDirect<Uri, Guid> { [Guid.Empty] = address };
            var document = mapper.Serialize(source);
            var result = new ReorderedDirect<Uri, Guid>();
            var reader = new BsonMapper(type => result);
            reader.Deserialize<IDictionary<Guid, Uri>>(document);
            Assert.Equal(address, ((IDictionary<Guid, Uri>)result)[Guid.Empty]);
        }

        [Fact]
        public void Unrelated_direct_generic_arguments_keep_legacy_object_mapping()
        {
            var mapper = new BsonMapper();
            var source = new GenericSide<int> { ["number"] = 42, ["text"] = "kept" };
            var result = mapper.Deserialize<GenericSide<int>>(mapper.Serialize(source));
            Assert.Equal(42, result["number"]);
            Assert.Equal("kept", result["text"]);
        }
    }
}
