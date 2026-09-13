using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class Dictionary_Tests
    {
        public class Dict
        {
            public IDictionary<DateTime, string> DateDict { get; set; } = new Dictionary<DateTime, string>();
        }

        private readonly BsonMapper _mapper = new BsonMapper();

        [Fact]
        public void Dictionary_Map()
        {
            var obj = new Dict();

            obj.DateDict[DateTime.Now] = "now!";

            var doc = _mapper.ToDocument(obj);

            var newobj = _mapper.ToObject<Dict>(doc);

            newobj.DateDict.Keys.First().Should().Be(obj.DateDict.Keys.First());
        }

        [Fact]
        public void Deserialize_Object()
        {
            var doc = new BsonDocument() { ["x"] = 1 };

            var result = _mapper.Deserialize(typeof(object), doc);
            Assert.Equal(typeof(Dictionary<string, object>), result.GetType());

            //! used to be empty
            var dic = (Dictionary<string, object>)result;
            Assert.Single(dic);
            Assert.Equal(1, dic["x"]);
        }

        [Fact]
        public void Deserialize_Hashtable()
        {
            var doc = new BsonDocument() { ["x"] = 1 };

            var result = _mapper.Deserialize(typeof(Hashtable), doc);
            Assert.Equal(typeof(Hashtable), result.GetType());

            //! used to be empty
            var dic = (Hashtable)result;
            Assert.Single(dic);
            Assert.Equal(1, dic["x"]);
        }

        [Fact]
        public void Serialize_Hashtable()
        {
            var data = new Hashtable() { ["x"] = 1 };

            //! used to fail
            var result = _mapper.Serialize(data).AsDocument;

            Assert.Single(result);
            Assert.Equal(1, result["x"].AsInt32);
        }

        [Fact]
        public void Deserialize_Uri()
        {
            var dict = new Dictionary<Uri, string>();
            dict.Add(new Uri("http://www.litedb.org/"), "LiteDB website");
            var doc = _mapper.Serialize(dict).AsDocument;
            var dict2 = _mapper.Deserialize<Dictionary<Uri, string>>(doc);

            Assert.Single(dict2);
            Assert.Equal(dict.Keys.Single(), dict2.Keys.Single());
        }
    }
}