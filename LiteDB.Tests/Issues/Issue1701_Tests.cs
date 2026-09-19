using System;
using System.Collections.Generic;
using Xunit;
using System.Linq;
using LiteDB.Tests.Utils;

namespace LiteDB.Tests.Issues
{
    public class Issue1701_Tests
    {
        [Fact]
        public void Deleted_Index_Slot_Test()
        {
            using var db = DatabaseFactory.Create();
            var col = db.GetCollection("col", BsonAutoId.Int32);
            var id = col.Insert(new BsonDocument { ["attr1"] = "attr", ["attr2"] = "attr", ["attr3"] = "attr" });

            col.EnsureIndex("attr1", "$.attr1");
            col.EnsureIndex("attr2", "$.attr2");
            col.EnsureIndex("attr3", "$.attr3");
            col.DropIndex("attr2");

            col.Update(id, new BsonDocument { ["attr1"] = "new" });
        }
    }
}
