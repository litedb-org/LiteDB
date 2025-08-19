using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class DropCollection_Tests
    {
        [Fact]
        public void DropCollection()
        {
            using (var file = new TempFile())
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollectionNames().Should().NotContain("col");

                var col = db.GetCollection("col");

                col.Insert(new BsonDocument {["a"] = 1});

                db.GetCollectionNames().Should().Contain("col");

                db.DropCollection("col");

                db.GetCollectionNames().Should().NotContain("col");
            }
        }

        [Fact]
        public void InsertDropCollection()
        {
            using (var file = new TempFile())
            {
                using (var db = new LiteDatabase(file.Filename))
                {
                    var col = db.GetCollection("test");
                    col.Insert(new BsonDocument { ["_id"] = 1 });
                    db.DropCollection("test");
                    db.Rebuild();
                }

                using (var db = new LiteDatabase(file.Filename))
                {
                    var col = db.GetCollection("test");
                    col.Insert(new BsonDocument { ["_id"] = 1 });
                }
            }
        }
    }
}