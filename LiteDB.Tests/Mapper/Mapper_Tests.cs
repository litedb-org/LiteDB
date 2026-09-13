using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Reflection;
using System;
using System.IO;


namespace LiteDB.Tests.Mapper
{
    [TestClass]
    public class Mapper_Tests
    {
        private BsonMapper _mapper = new BsonMapper();

        [TestMethod]
        public void ToDocument_ReturnsNull_WhenFail()
        {
            var array = new int[] { 1, 2, 3, 4, 5 };
            var doc1 = _mapper.ToDocument(array);
            Assert.IsNull(doc1);

            var doc2 = _mapper.ToDocument(typeof(int[]), array);
            Assert.IsNull(doc2);
        }

        [TestMethod]
        public void Class_Not_Assignable()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var col = db.GetCollection<MyClass>("Test");
                col.Insert(new MyClass { Id = 1, Member = null });
                var type = typeof(OtherClass);
                var typeName = type.FullName + ", " + type.GetTypeInfo().Assembly.GetName().Name;

                var bsonDocumentCollection = db.GetCollection("Test");
                var bsonDocument = bsonDocumentCollection.FindById(1);
                bsonDocument["_type"] = typeName;

                bsonDocumentCollection.Update(bsonDocument);

                Func<MyClass> func = (() => col.FindById(1));
                Assert.ThrowsException<LiteException>(func);
            }
        }

        public class MyClass
        {
            public int Id { get; set; }
            public MyClass Member { get; set; }
        }

        public class OtherClass
        {
            public string Name { get; set; }
        }
    }
}
