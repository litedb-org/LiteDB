using System.IO;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2190_Tests
    {
        [Fact]
        public void BsonDocument_GetElements_Should_Not_Duplicate_CaseVariant_Id_Key()
        {
            var doc = new BsonDocument();
            doc["_Id"] = 1;
            doc["Played"] = 0;

            Assert.Equal(2, doc.Count);

            var elements = doc.GetElements().ToArray();

            Assert.Equal(2, elements.Length);
            Assert.Equal("_id", elements[0].Key);
            Assert.Equal("Played", elements[1].Key);
            Assert.Equal(0, elements[1].Value.AsInt32);
        }

        [Fact]
        public void Insert_Should_Not_Corrupt_Field_Name_When_Type_Has_UnderscoreId_Property()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var collection = db.GetCollection<Statistic>("dos_statistic");

            collection.Insert(new Statistic { _Id = 1 });

            var raw = db.GetCollection("dos_statistic").FindById(1);

            Assert.NotNull(raw);
            Assert.True(raw.ContainsKey("Played"));
            Assert.False(raw.ContainsKey("Pla"));
            Assert.Equal(0, raw["Played"].AsInt32);
        }

        public class Statistic
        {
            public int _Id { get; set; }

            public int Played { get; set; } = 0;
        }
    }
}
