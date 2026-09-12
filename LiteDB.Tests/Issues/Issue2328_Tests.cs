using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2328_Tests
    {
        public class Warehouse { public double Value { get; set; } }
        public class Row
        {
            public int Id { get; set; }
            public Warehouse Property { get; set; }
        }
        private static BsonMapper Mapper()
        {
            var mapper = new BsonMapper();
            mapper.RegisterType<Warehouse>(x => new BsonDocument { ["type"] = "Warehouse", ["Value"] = x.Value },
                x => new Warehouse { Value = x["Value"].AsDouble });
            return mapper;
        }

        [Fact]
        public void Custom_registered_double_max_value_survives_storage_and_SQL_without_null()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename, Mapper()))
            {
                db.GetCollection<Row>("rows").Insert(new Row { Id = 1, Property = new Warehouse { Value = double.MaxValue } });
            }
            using (var db = new LiteDatabase(file.Filename, Mapper()))
            {
                var value = db.GetCollection<Row>("rows").FindById(1).Property.Value;
                BitConverter.DoubleToInt64Bits(value).Should().Be(BitConverter.DoubleToInt64Bits(double.MaxValue));
                var raw = db.GetCollection("rows").FindById(1)["Property"];
                raw["type"].AsString.Should().Be("Warehouse");
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
