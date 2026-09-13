using System;
using System.Globalization;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Document
{
    public class Json_Tests
    {
        private BsonDocument CreateDoc()
        {
            // create same object, but using BsonDocument
            var doc = new BsonDocument();
            doc["_id"] = 123;
            doc["Special"] = "Màçã ámö-î";
            doc["FirstString"] = "BEGIN this string \" has \" \t and this \f \n\r END";
            doc["CustomerId"] = Guid.NewGuid();
            doc["Date"] = new DateTime(2015, 1, 1);
            doc["MyNull"] = null;
            doc["Items"] = new BsonArray();
            doc["MyObj"] = new BsonDocument();
            doc["EmptyString"] = "";
            var obj = new BsonDocument();
            obj["Qtd"] = 3;
            obj["Description"] = "Big beer package";
            obj["Unit"] = 1299.995;
            doc["Items"].AsArray.Add(obj);
            doc["Items"].AsArray.Add("string-one");
            doc["Items"].AsArray.Add(null);
            doc["Items"].AsArray.Add(true);
            doc["Items"].AsArray.Add(DateTime.Now);


            return doc;
        }

        [Fact]
        public void Json_To_Document()
        {
            var o = CreateDoc();

            var json = JsonSerializer.Serialize(o);

            var doc = JsonSerializer.Deserialize(json).AsDocument;

            doc["Special"].AsString.Should().Be(o["Special"].AsString);
            doc["Date"].AsDateTime.Should().Be(o["Date"].AsDateTime);
            doc["CustomerId"].AsGuid.Should().Be(o["CustomerId"].AsGuid);
            doc["Items"].AsArray.Count.Should().Be(o["Items"].AsArray.Count);
            doc["_id"].AsInt32.Should().Be(123);
            doc["_id"].AsInt64.Should().Be(o["_id"].AsInt64);
        }

        [Fact]
        public void JsonWriterTest()
        {
            var specialChars = "ÁÀÃÂÄÉÈÊËÉÍÌÎÏÓÒÕÔÖÚÙÛÜÇáàãâäéèêëéíìîïóòõôöúùûüç";
            JsonSerializer.Serialize(specialChars).Should().Be('\"' + specialChars + '\"');
        }

        [Fact]
        public void Json_Number_Deserialize_Test()
        {
            int positiveInt32 = 5000000;
            int negativeInt32 = -5000000;
            long positiveInt64 = 210000000000L;
            long negativeInt64 = -210000000000L;
            double positiveDouble = 210000000000D;
            double negativeDouble = -210000000000D;

            JsonSerializer.Deserialize(positiveInt32.ToString()).Should().Be(positiveInt32);
            JsonSerializer.Deserialize(negativeInt32.ToString()).Should().Be(negativeInt32);
            JsonSerializer.Deserialize(positiveInt64.ToString()).Should().Be(positiveInt64);
            JsonSerializer.Deserialize(negativeInt64.ToString()).Should().Be(negativeInt64);
            JsonSerializer.Deserialize(positiveDouble.ToString("F1", CultureInfo.InvariantCulture)).Should().Be(positiveDouble);
            JsonSerializer.Deserialize(negativeDouble.ToString("F1", CultureInfo.InvariantCulture)).Should().Be(negativeDouble);
        }

        [Fact]
        public void Json_DoubleNaN_Tests()
        {
            BsonDocument doc = new BsonDocument();
            doc["doubleNaN"] = double.NaN;
            doc["doubleNegativeInfinity"] = double.NegativeInfinity;
            doc["doublePositiveInfinity"] = double.PositiveInfinity;

            // Convert to JSON
            string json = JsonSerializer.Serialize(doc);

            var bson = JsonSerializer.Deserialize(json);

            // JSON standard converts NaN and Infinities to null, so deserialized values should not be double.NaN nor double.*Infinity
            Assert.False(double.IsNaN(bson["doubleNaN"].AsDouble));
            Assert.False(double.IsNegativeInfinity(bson["doubleNegativeInfinity"].AsDouble));
            Assert.False(double.IsPositiveInfinity(bson["doublePositiveInfinity"].AsDouble));
        }
    }
}