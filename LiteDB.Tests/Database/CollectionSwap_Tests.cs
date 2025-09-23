using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LiteDB.Tests.Database
{
    [TestClass]
    public class CollectionSwap_Tests
    {
        private const string Collectioname = "Hempels";
        private class A
        {
            public string Name { get; set; }
        }

        private class B
        {
            public string Element { get; set; }
        }

        [TestMethod, TestCategory("Database")]
        public void Collection_Swap()
        {
            using (var db = new LiteDatabase(@"demo.db"))
            {
                // Preparing
                var col = db.GetCollection<A>(Collectioname);
                col.Insert(new BsonValue("A"),new A {Name = "A"});
                var col2 = db.GetCollection<B>(Collectioname);
                var testElement = new B {Element = "B"};
                col2.Insert(new BsonValue("B"), testElement);

                var col3 = col.Swap<B>();
                var belement = col3.FindById(new BsonValue("B"));
                Assert.AreEqual(testElement.Element, belement.Element);
            }
        }
    }
}
