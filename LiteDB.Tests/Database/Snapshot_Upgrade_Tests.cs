using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class Snapshot_Upgrade_Tests
    {
        [Fact]
        public void Transaction_Update_Upsert()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection("test");

            bool transactionCreated = db.BeginTrans();
            Assert.True(transactionCreated);

            int updatedDocs = col.UpdateMany("{name: \"xxx\"}", BsonExpression.Create("_id = 1"));
            Assert.Equal(0, updatedDocs);

            col.Upsert(new BsonDocument() { ["_id"] = 1, ["name"] = "xxx" });
            var result = col.FindById(1);
            Assert.NotNull(result);
        }
    }
}