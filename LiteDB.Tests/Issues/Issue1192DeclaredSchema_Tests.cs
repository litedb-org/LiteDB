using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1192DeclaredSchema_Tests
    {
        public enum ByteEnum : byte { First = 1 }
        public class BaseValue { }
        public class DerivedValue : BaseValue { }
        public interface ILegacy<TKey, TValue> : IDictionary { }
        public interface ISide<TKey, TValue> : IDictionary<Guid, string> { }
        public class Legacy<TKey, TValue> : Hashtable, ILegacy<TKey, TValue> { }
        public class Side<TKey, TValue> : Issue1192DictionaryContracts_Tests.HybridDictionary, ISide<TKey, TValue> { }
        public class OpaqueBase<TTag, TKey, TValue> : Hashtable, IDictionary
        {
            object IDictionary.this[object key] { get => base[key]; set => base[key] = value; }
        }
        public class OpaqueDerived<TKey, TValue> : OpaqueBase<int, TKey, TValue> { }

        public class GenericSideBase<TKey, TValue> : Issue1192DictionaryContracts_Tests.HybridDictionary, IDictionary
        {
            object IDictionary.this[object key] { get => base[key]; set => base[key] = value; }
        }
        public class TaggedSide<TKey, TValue> : GenericSideBase<Guid, string> { }
        public interface IEnumMap : IDictionary<string, ByteEnum[]> { }
        public class EnumMap : Dictionary<string, ByteEnum[]>, IEnumMap { }
        public class ValueMap : Dictionary<string, BaseValue> { }
        public class SortedMap : SortedDictionary<Guid, Uri> { }
        public class SortedListMap : SortedList<Guid, Uri> { }
        public class ConcurrentMap : ConcurrentDictionary<Guid, Uri> { }

        [Fact]
        public void Non_generic_declarations_preserve_object_value_serialization_shapes()
        {
            var mapper = new BsonMapper();
            var source = new EnumMap { ["value"] = new[] { ByteEnum.First } };
            Assert.True(mapper.Serialize(source).AsDocument["value"].IsBinary);
            Assert.True(mapper.Serialize((object)source).AsDocument["value"].IsBinary);
            Assert.True(mapper.Serialize((IEnumMap)source).AsDocument["value"].IsBinary);
            mapper.RegisterType<BaseValue>(value => "custom", value => new BaseValue());
            Assert.True(mapper.Serialize(new ValueMap { ["value"] = new DerivedValue() }).AsDocument["value"].IsDocument);
        }

        [Fact]
        public void Inherited_custom_indexer_does_not_infer_its_generic_side_store_schema()
        {
            var mapper = new BsonMapper();
            mapper.RegisterType<string>(value => value, value => value.AsString);
            var source = new TaggedSide<string, int> { ["number"] = 42 };
            var result = mapper.Deserialize<TaggedSide<string, int>>(mapper.Serialize(source));
            Assert.Equal(42, result["number"]);
        }

        [Theory]
        [InlineData(typeof(SortedMap))]
        [InlineData(typeof(SortedListMap))]
        [InlineData(typeof(ConcurrentMap))]
        public void Standard_dictionary_subclasses_keep_the_implementations_key_value_schema(Type type)
        {
            var source = (IDictionary)Activator.CreateInstance(type);
            var address = new Uri("https://example.test/standard");
            source[Guid.Empty] = address;
            var mapper = new BsonMapper();
            var result = (IDictionary)mapper.Deserialize(type, mapper.Serialize(type, source));
            Assert.Equal(address, result[Guid.Empty]);
        }

        [Fact]
        public void Object_declared_dictionary_retains_its_value_serializer_and_enum_array_shape()
        {
            var mapper = new BsonMapper();
            mapper.RegisterType<BaseValue>(value => "custom", value => new BaseValue());
            object custom = new Dictionary<string, BaseValue> { ["value"] = new DerivedValue() };
            Assert.Equal("custom", mapper.Serialize(custom).AsDocument["value"].AsString);
            object enums = new Dictionary<string, ByteEnum[]> { ["value"] = new[] { ByteEnum.First } };
            Assert.True(mapper.Serialize(enums).AsDocument["value"].IsArray);
        }

        [Fact]
        public void Opaque_generic_interfaces_preserve_their_declared_schema()
        {
            var address = new Uri("https://example.test/declared");
            var legacy = new Legacy<Guid, Uri> { [Guid.Empty] = address };
            var mapper = new BsonMapper(type => new Legacy<Guid, Uri>());
            var document = mapper.Serialize((ILegacy<Guid, Uri>)legacy);
            var result = mapper.Deserialize<ILegacy<Guid, Uri>>(document);
            Assert.Equal(address, result[Guid.Empty]);

            var side = new Side<string, Uri>();
            var sideMapper = new BsonMapper(type => side);
            sideMapper.Deserialize<ISide<string, Uri>>(new BsonDocument { ["address"] = address.AbsoluteUri });
            Assert.Equal(address, side["address"]);
        }

        [Fact]
        public void Opaque_generic_base_tags_do_not_override_the_concrete_legacy_schema()
        {
            var mapper = new BsonMapper();
            var address = new Uri("https://example.test/base");
            var source = new OpaqueDerived<Guid, Uri> { [Guid.Empty] = address };
            var result = mapper.Deserialize<OpaqueDerived<Guid, Uri>>(mapper.Serialize(source));
            Assert.Equal(address, result[Guid.Empty]);
        }
    }
}
