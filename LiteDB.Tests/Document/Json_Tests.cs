﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace LiteDB.Tests.Document
{
    [TestClass]
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
            doc["Items"] = new BsonValue();
            doc["MyObj"] = new BsonDocument();
            doc["EmptyString"] = "";
            var obj = new BsonDocument();
            obj["Qtd"] = 3;
            obj["Description"] = "Big beer package";
            obj["Unit"] = 1299.995;
            doc["Items"].Add(obj);
            doc["Items"].Add("string-one");
            doc["Items"].Add(null);
            doc["Items"].Add(true);
            doc["Items"].Add(DateTime.Now);


            return doc;
        }

        [TestMethod]
        public void Json_To_Document()
        {
            var o = CreateDoc();

            var json = JsonSerializer.Serialize(o);

            var doc = JsonSerializer.Deserialize(json).AsDocument;

            Assert.AreEqual(o["Special"].AsString, doc["Special"].AsString);
            Assert.AreEqual(o["Date"].AsDateTime, doc["Date"].AsDateTime);
            Assert.AreEqual(o["CustomerId"].AsGuid, doc["CustomerId"].AsGuid);
            Assert.AreEqual(o["Items"].Count, doc["Items"].Count);
            Assert.AreEqual(123, doc["_id"].AsInt32);
            Assert.AreEqual(o["_id"].AsInt64, doc["_id"].AsInt64);
        }
    }
}