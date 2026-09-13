using System;
using System.Collections.Generic;
using System.Text;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Internals
{
    public class ExtendedLength_Tests
    {
        [Fact]
        public void ExtendedLengthHelper_Tests()
        {
            byte typeByte, lengthByte;
            BsonType type;
            ushort length;
            ExtendedLengthHelper.WriteLength(BsonType.String, 1010, out typeByte, out lengthByte);
            ExtendedLengthHelper.ReadLength(typeByte, lengthByte, out type, out length);
            Assert.Equal(BsonType.String, type);
            Assert.Equal((ushort)1010, length);
        }

        [Fact]
        public void IndexExtendedLength_Tests()
        {
            using var db = DatabaseFactory.Create();
            var col = db.GetCollection("customers", BsonAutoId.Int32);
            col.EnsureIndex("$.Name");
            col.Insert(new BsonDocument { ["Name"] = new string('A', 1010) });
            col.Insert(new BsonDocument { ["Name"] = new string('B', 230) });

            var results = db.Execute("select $ from customers where $.Name < 'B'").ToArray();
            Assert.Single(results);
        }
    }
}
