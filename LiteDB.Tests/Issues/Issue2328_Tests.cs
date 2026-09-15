using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2328_Tests
    {
        public class Warehouse
        {
            public DateTime Updated { get; set; }
            public DateTime Nexted { get; set; }
            public double Value { get; set; }
            public decimal Pure { get; set; }
            public int Count { get; set; }
        }
        public class Row
        {
            public int Id { get; set; }
            public Warehouse Property { get; set; }
        }
        private static BsonMapper Mapper()
        {
            var mapper = new BsonMapper();
            mapper.RegisterType<Warehouse>(x => new BsonDocument
            {
                ["type"] = "Warehouse", ["Updated"] = x.Updated, ["Nexted"] = x.Nexted,
                ["Value"] = x.Value, ["Pure"] = x.Pure, ["Count"] = x.Count
            }, x => !x["type"].IsNull && x["type"] == "Warehouse" ? new Warehouse
            {
                Updated = x["Updated"], Nexted = x["Nexted"], Value = x["Value"],
                Pure = x["Pure"], Count = x["Count"]
            } : throw new ArgumentOutOfRangeException(nameof(x)));
            return mapper;
        }

        [Fact]
        public void Custom_registered_double_max_value_survives_storage_and_SQL_without_null()
        {
            using var file = new TempFile();
            var expected = new Warehouse
            {
                Updated = new DateTime(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                Nexted = new DateTime(2023, 6, 7, 8, 9, 10, DateTimeKind.Utc),
                Value = double.MaxValue, Pure = 123.456789m, Count = 27
            };
            using (var db = new LiteDatabase(file.Filename, Mapper()))
            {
                db.UtcDate = true;
                db.GetCollection<Row>("rows").Insert(new Row { Id = 1, Property = expected });
            }
            using (var db = new LiteDatabase(file.Filename, Mapper()))
            {
                db.UtcDate = true;
                var property = db.GetCollection<Row>("rows").FindById(1).Property;
                property.Should().BeEquivalentTo(expected);
                var value = property.Value;
                BitConverter.DoubleToInt64Bits(value).Should().Be(BitConverter.DoubleToInt64Bits(double.MaxValue));
                var raw = db.GetCollection("rows").FindById(1)["Property"];
                raw["type"].AsString.Should().Be("Warehouse");
                raw["Updated"].AsDateTime.Should().Be(expected.Updated);
                raw["Nexted"].AsDateTime.Should().Be(expected.Nexted);
                raw["Pure"].AsDecimal.Should().Be(expected.Pure);
                raw["Count"].AsInt32.Should().Be(expected.Count);
                var binary = BsonSerializer.Deserialize(BsonSerializer.Serialize(raw.AsDocument));
                binary["Value"].Type.Should().Be(BsonType.Double);
                BitConverter.DoubleToInt64Bits(binary["Value"].AsDouble).Should().Be(BitConverter.DoubleToInt64Bits(expected.Value));
                raw["Value"].Type.Should().Be(BsonType.Double);
                raw["Value"].AsDouble.Should().Be(double.MaxValue);
                using var reader = db.Execute("SELECT Property.Value AS value FROM rows WHERE _id = 1");
                reader.Read().Should().BeTrue();
                reader.Current["value"].AsDouble.Should().Be(double.MaxValue);
                reader.Read().Should().BeFalse();
            }
        }
    }
}
