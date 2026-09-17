using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1162_Tests
    {
        [Fact]
        public void Dynamic_expando_list_keeps_keys_values_and_distinct_rows()
        {
            var first = new ExpandoObject();
            var firstValues = (IDictionary<string, object>)first;
            firstValues["Test"] = "Yes";
            firstValues["Value"] = 123;
            var second = new ExpandoObject();
            var secondValues = (IDictionary<string, object>)second;
            secondValues["Test"] = "No";
            secondValues["Value"] = 456;
            var mapper = new BsonMapper();
            var input = new List<ExpandoObject> { first, second };
            var documents = input.Select(x => mapper.ToDocument(x.GetType(), x)).ToList();
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").InsertBulk(documents).Should().Be(2);
            var rows = db.GetCollection("rows").FindAll().OrderBy(x => x["Value"].AsInt32).ToArray();
            rows.Select(x => x["Test"].AsString).Should().Equal("Yes", "No");
            rows.Select(x => x["Value"].AsInt32).Should().Equal(123, 456);
            firstValues["Value"].Should().Be(123);
        }
        [Fact]
        public void Expando_collection_round_trips_nested_values_without_mutating_input()
        {
            var nested = new ExpandoObject();
            ((IDictionary<string, object>)nested)["number"] = 42L;
            var input = new ExpandoObject();
            var values = (IDictionary<string, object>)input;
            values["_id"] = 7;
            values["child"] = nested;
            values["items"] = new object[] { nested, null, " text " };
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<ExpandoObject>("rows");
            rows.Insert(input).Should().Be((BsonValue)7);
            var result = (IDictionary<string, object>)rows.FindById(7);
            ((IDictionary<string, object>)result["child"])["number"].Should().Be(42L);
            var items = ((IEnumerable<object>)result["items"]).ToArray();
            ((IDictionary<string, object>)items[0])["number"].Should().Be(42L);
            items[1].Should().BeNull();
            items[2].Should().Be("text");
            values["child"].Should().BeSameAs(nested);
            ((object[])values["items"])[2].Should().Be(" text ");
        }

        [Fact]
        public void Expando_dictionary_members_support_ContainsKey_queries()
        {
            IDictionary<string, object> values = new ExpandoObject();
            values["key"] = null;
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Issue1376_Tests.InterfaceRow>();
            rows.Insert(new Issue1376_Tests.InterfaceRow { Id = 1, Data = values });
            rows.Find(row => row.Data.ContainsKey("key")).Select(row => row.Id).Should().Equal(1);
            rows.Find(row => row.Data.ContainsKey("missing")).Should().BeEmpty();
        }

        [Fact]
        public void Expando_serialization_honors_custom_serializers_and_depth_limit()
        {
            var input = new ExpandoObject();
            var mapper = new BsonMapper();
            mapper.RegisterType<ExpandoObject>(_ => new BsonDocument { ["custom"] = true }, _ => input);
            mapper.ToDocument(input)["custom"].AsBoolean.Should().BeTrue();
            mapper.ToObject<ExpandoObject>(new BsonDocument()).Should().BeSameAs(input);
            ((IDictionary<string, object>)input)["self"] = input;
            Action serialize = () => new BsonMapper { MaxDepth = 5 }.ToDocument(input);
            serialize.Should().Throw<LiteException>();
        }
        [Fact]
        public void Case_variant_expando_members_are_rejected_without_losing_values()
        {
            var input = new ExpandoObject();
            var values = (IDictionary<string, object>)input;
            values["Name"] = "first";
            values["name"] = "second";
            Action serialize = () => new BsonMapper().ToDocument(input);
            serialize.Should().Throw<LiteException>().WithMessage("*same BSON field*");
            values["Name"].Should().Be("first");
            values["name"].Should().Be("second");
        }
        [Theory]
        [InlineData("widget")]
        [InlineData("System.String, mscorlib")]
        public void Typed_expando_preserves_a_type_named_member(string typeName)
        {
            var input = new ExpandoObject();
            var values = (IDictionary<string, object>)input;
            values["_id"] = 9;
            values["_type"] = typeName;
            values["value"] = 42;
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<ExpandoObject>("rows");
            rows.Insert(input);
            var result = (IDictionary<string, object>)rows.FindById(9);
            result["_type"].Should().Be(typeName);
            result["value"].Should().Be(42);
        }

        public class EnumerableRow
        {
            public IEnumerable<KeyValuePair<string, object>> Values { get; set; }
        }

        public class CollectionRow
        {
            public ICollection<KeyValuePair<string, object>> Values { get; set; }
        }

        [Fact]
        public void Expando_declared_as_an_enumerable_preserves_its_array_contract()
        {
            var input = new ExpandoObject();
            ((IDictionary<string, object>)input)["key"] = 42;
            var mapper = new BsonMapper();
            var enumerable = mapper.ToDocument(new EnumerableRow { Values = input });
            enumerable["Values"].IsArray.Should().BeTrue();
            mapper.ToObject<EnumerableRow>(enumerable).Values.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, object>("key", 42));
            var collection = mapper.ToDocument(new CollectionRow { Values = input });
            collection["Values"].IsArray.Should().BeTrue();
            mapper.ToObject<CollectionRow>(collection).Values.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, object>("key", 42));
        }
    }
}
