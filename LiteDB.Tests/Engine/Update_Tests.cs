using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class Update_Tests
    {
        [Fact]
        public void Update_IndexNodes()
        {
            using (var db = new LiteEngine())
            {
                var doc = new BsonDocument {["_id"] = 1, ["name"] = "Mauricio", ["phones"] = new BsonArray() {"51", "11"}};

                db.Insert("col1", doc);

                db.EnsureIndex("col1", "idx_name", "name", false);
                db.EnsureIndex("col1", "idx_phones", "phones[*]", false);

                doc["name"] = "David";
                doc["phones"] = new BsonArray() {"11", "25"};

                db.Update("col1", doc);

                doc["name"] = "John";

                db.Update("col1", doc);
            }
        }

        [Fact]
        public void Update_ExtendBlocks()
        {
            using (var db = new LiteEngine())
            {
                var doc = new BsonDocument {["_id"] = 1, ["d"] = new byte[1000]};

                db.Insert("col1", doc);

                // small (same page)
                doc["d"] = new byte[300];

                db.Update("col1", doc);

                var page3 = db.GetPageLog(3);

                page3["freeBytes"].AsInt32.Should().Be(7828);

                // big (same page)
                doc["d"] = new byte[2000];

                db.Update("col1", doc);

                page3 = db.GetPageLog(3);

                page3["freeBytes"].AsInt32.Should().Be(6128);

                // big (extend page)
                doc["d"] = new byte[20000];

                db.Update("col1", doc);

                page3 = db.GetPageLog(3);
                var page4 = db.GetPageLog(4);
                var page5 = db.GetPageLog(5);

                page3["freeBytes"].AsInt32.Should().Be(0);
                page4["freeBytes"].AsInt32.Should().Be(0);
                page5["freeBytes"].AsInt32.Should().Be(4428);

                // small (shrink page)
                doc["d"] = new byte[10000];

                db.Update("col1", doc);

                page3 = db.GetPageLog(3);
                page4 = db.GetPageLog(4);
                page5 = db.GetPageLog(5);

                page3["freeBytes"].AsInt32.Should().Be(0);
                page4["freeBytes"].AsInt32.Should().Be(6278);
                page5["pageType"].AsString.Should().Be("Empty");
            }
        }

        [Fact]
        public void Update_Empty_Collection()
        {
            using(var e = new LiteEngine())
            {
                var d = new BsonDocument { ["_id"] = 1, ["a"] = "demo" };
                var r = e.Update("col1", new BsonDocument[] { d });

                r.Should().Be(0);
            }
        }
    }
}