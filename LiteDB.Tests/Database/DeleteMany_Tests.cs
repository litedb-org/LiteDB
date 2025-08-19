using System;
using System.IO;
using System.Linq;
using LiteDB;
using AwesomeAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class DeleteMany_Tests
    {
        [Fact]
        public void DeleteMany_With_Arguments()
        {
            using (var db = new LiteDatabase(":memory:"))
            {
                var c1 = db.GetCollection("Test");

                var d1 = new BsonDocument() { ["_id"] = 1, ["p1"] = 1 };
                c1.Insert(d1);

                c1.Count().Should().Be(1);

                // try BsonExpression predicate with argument - not deleted
                var e1 = BsonExpression.Create("$._id = @0", 1);
                var r1 = c1.DeleteMany(e1);

                r1.Should().Be(1);

                // the same BsonExpression predicate works fine in FindOne
                var r = c1.FindOne(e1);

            }
        }
    }
}