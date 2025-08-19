using System;
using System.Linq;
using System.Collections.Generic;
using AwesomeAssertions;
using Xunit;

namespace LiteDB.Internals
{
    public class Document_Test
    {
        [Fact]
        public void Document_Copies_Properties_To_KeyValue_Array()
        {
            // ARRANGE
            // create a Bson document with all possible value types

            var document = new BsonDocument();
            document.Add("string", new BsonValue("string"));
            document.Add("bool", new BsonValue(true));
            document.Add("objectId", new BsonValue(ObjectId.NewObjectId()));
            document.Add("DateTime", new BsonValue(DateTime.Now));
            document.Add("decimal", new BsonValue((decimal) 1));
            document.Add("double", new BsonValue((double) 1.0));
            document.Add("guid", new BsonValue(Guid.NewGuid()));
            document.Add("int", new BsonValue((int) 1));
            document.Add("long", new BsonValue((long) 1));
            document.Add("bytes", new BsonValue(new byte[] {(byte) 1}));
            document.Add("bsonDocument", new BsonDocument());

            // ACT
            // copy all properties to destination array

            var result = new KeyValuePair<string, BsonValue>[document.Count()];
            document.CopyTo(result, 0);
        }

        [Fact]
        public void Value_Index_From_BsonValue()
        {
            var arr = JsonSerializer.Deserialize("[0, 1, 2, 3]");
            var doc = JsonSerializer.Deserialize("{a:1,b:2,c:3}");

            arr[0].RawValue.Should().Be(0);
            arr[3].RawValue.Should().Be(3);

            doc["a"].RawValue.Should().Be(1);
            doc["c"].RawValue.Should().Be(3);

            arr[1] = 111;
            doc["b"] = 222;

            arr[1].RawValue.Should().Be(111);
            doc["b"].RawValue.Should().Be(222);
        }
    }
}