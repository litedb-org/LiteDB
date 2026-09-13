using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Document
{
    public class Bson_Tests
    {
        private BsonDocument CreateDoc()
        {
            // create same object, but using BsonDocument
            var doc = new BsonDocument();
            doc["_id"] = 123;
            doc["Address"] = new BsonDocument {["city"] = "Atlanta", ["state"] = "XY"};
            doc["FirstString"] = "BEGIN this string \" has \" \t and this \f \n\r END";
            doc["CustomerId"] = Guid.NewGuid();
            doc["Phone"] = new BsonDocument {["Mobile"] = "999", ["LandLine"] = "777"};
            doc["Date"] = DateTime.Now;
            doc["MyNull"] = null;
            doc["EmptyObj"] = new BsonDocument();
            doc["EmptyString"] = "";
            doc["maxDate"] = DateTime.MaxValue;
            doc["minDate"] = DateTime.MinValue;


            doc["Items"] = new BsonArray();

            doc["Items"].AsArray.Add(new BsonDocument());
            doc["Items"].AsArray[0].AsDocument["Qtd"] = 3;
            doc["Items"].AsArray[0].AsDocument["Description"] = "Big beer package";
            doc["Items"].AsArray[0].AsDocument["Unit"] = (double) 10 / (double) 3;

            doc["Items"].AsArray.Add("string-one");
            doc["Items"].AsArray.Add(null);
            doc["Items"].AsArray.Add(true);
            doc["Items"].AsArray.Add(DateTime.Now);

            doc["Last"] = 999;

            return doc;
        }

        [Fact]
        public void Convert_To_Json_Bson()
        {
            var o = CreateDoc();

            var bson = BsonSerializer.Serialize(o);
            var json = JsonSerializer.Serialize(o);

            var doc = BsonSerializer.Deserialize(bson);

            doc["_id"].AsInt32.Should().Be(123);
            doc["_id"].AsInt64.Should().Be(o["_id"].AsInt64);

            doc["FirstString"].AsString.Should().Be(o["FirstString"].AsString);
            doc["Date"].AsDateTime.ToString().Should().Be(o["Date"].AsDateTime.ToString());
            doc["CustomerId"].AsGuid.Should().Be(o["CustomerId"].AsGuid);
            doc["EmptyString"].AsString.Should().Be(o["EmptyString"].AsString);

            doc["maxDate"].AsDateTime.Should().Be(DateTime.MaxValue);
            doc["minDate"].AsDateTime.Should().Be(DateTime.MinValue);

            doc["Items"].AsArray.Count.Should().Be(o["Items"].AsArray.Count);
            doc["Items"].AsArray[0].AsDocument["Unit"].AsDouble.Should().Be(o["Items"].AsArray[0].AsDocument["Unit"].AsDouble);
            doc["Items"].AsArray[4].AsDateTime.ToString().Should().Be(o["Items"].AsArray[4].AsDateTime.ToString());
        }

        [Fact]
        public void Bson_Using_UTC_Local_Dates()
        {
            var doc = new BsonDocument {["now"] = DateTime.Now, ["min"] = DateTime.MinValue, ["max"] = DateTime.MaxValue};
            var bytes = BsonSerializer.Serialize(doc);

            var local = BsonSerializer.Deserialize(bytes, false);
            var utc = BsonSerializer.Deserialize(bytes, true);

            // local test
            local["min"].AsDateTime.Should().Be(DateTime.MinValue);
            local["max"].AsDateTime.Should().Be(DateTime.MaxValue);
            local["now"].AsDateTime.Kind.Should().Be(DateTimeKind.Local);

            // utc test
            utc["min"].AsDateTime.Should().Be(DateTime.MinValue);
            utc["max"].AsDateTime.Should().Be(DateTime.MaxValue);
            utc["now"].AsDateTime.Kind.Should().Be(DateTimeKind.Utc);
        }

        [Fact]
        public void Bson_Partial_Deserialize()
        {
            var src = this.CreateDoc();
            var bson = BsonSerializer.Serialize(src);

            // read only _id and string
            var doc1 = BsonSerializer.Deserialize(bson, false, new HashSet<string>(new string[] {"_id", "FirstString"}));

            doc1["_id"].AsInt32.Should().Be(src["_id"].AsInt32);
            doc1["FirstString"].AsString.Should().Be(src["FirstString"].AsString);

            // read only 2 sub documents
            var doc2 = BsonSerializer.Deserialize(bson, false, new HashSet<string>(new string[] {"Address", "Date"}));

            doc2["Address"].AsDocument.ToString().Should().Be(src["Address"].AsDocument.ToString());
            doc2["Date"].AsDateTime.Should().Be(src["Date"].AsDateTime);

            // read only last field
            var doc3 = BsonSerializer.Deserialize(bson, false, new HashSet<string>(new string[] {"Last"}));

            doc3["Last"].AsInt32.Should().Be(src["Last"].AsInt32);

            // read all document
            var doc4 = BsonSerializer.Deserialize(bson, false);

            doc4.ToString().Should().Be(src.ToString());
        }

        [Fact]
        public void BsonMapper_AnonymousType()
        {
            var mapper = new BsonMapper();

            var obj = new
            {
                Id = 1,
                Name = "John"
            };

            var doc = mapper.ToDocument(obj);
            var obj2 = DeserializeAnonymous(mapper, doc, obj);

            Assert.Equal(obj.Id, obj2.Id);
            Assert.Equal(obj.Name, obj2.Name);

            static T DeserializeAnonymous<T>(BsonMapper mapper, BsonDocument doc, T obj)
            {
                return mapper.Deserialize<T>(doc);
            }
        }
    }
}