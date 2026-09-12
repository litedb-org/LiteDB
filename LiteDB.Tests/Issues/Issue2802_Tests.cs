using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2802_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }
        public class CustomMapper : BsonMapper
        {
            public int Reads { get; private set; }
            public override object ToObject(Type type, BsonDocument doc)
            {
                var result = base.ToObject(type, doc);
                if (result is Row row)
                {
                    Reads++;
                    row.Name = "decoded:" + row.Name;
                }
                return result;
            }
        }

        [Theory]
        [InlineData("FindAll")]
        [InlineData("FindById")]
        [InlineData("Find")]
        [InlineData("FindOne")]
        [InlineData("Query")]
        public void Typed_read_uses_virtual_mapper_and_returns_its_result(string api)
        {
            var mapper = new CustomMapper();
            using var db = new LiteDatabase(":memory:", mapper);
            var col = db.GetCollection<Row>("rows");
            col.Insert(new Row { Id = 1, Name = "one" });
            Row result;
            switch (api)
            {
                case "FindAll": result = col.FindAll().Single(); break;
                case "FindById": result = col.FindById(1); break;
                case "Find": result = col.Find(x => x.Id == 1).Single(); break;
                case "FindOne": result = col.FindOne(x => x.Id == 1); break;
                default: result = col.Query().ToList().Single(); break;
            }
            result.Id.Should().Be(1);
            result.Name.Should().Be("decoded:one", "calling and discarding the override's result is insufficient");
            mapper.Reads.Should().Be(1);
            db.GetCollection("rows").FindById(1)["Name"].AsString.Should().Be("one");
        }
    }
}
