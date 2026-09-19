using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2802_Tests
    {
        [Fact]
        public void Raw_read_returns_replacement_from_deserialization_callback_once()
        {
            var mapper = new BsonMapper();
            var reads = 0;
            var replacement = new BsonDocument { ["_id"] = 1, ["Name"] = "decoded" };
            mapper.OnDeserialization = (sender, type, value) =>
            {
                type.Should().Be(typeof(BsonDocument));
                reads++;
                return replacement;
            };

            using var db = new LiteDatabase(":memory:", mapper);
            var collection = db.GetCollection("rows");
            collection.Insert(new BsonDocument { ["_id"] = 1, ["Name"] = "stored" });

            ((object)collection.FindById(1)).Should().BeSameAs(replacement);
            reads.Should().Be(1);
            mapper.OnDeserialization = null;
            collection.FindById(1)["Name"].AsString.Should().Be("stored");
        }

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

        public class GenericOnlyRow
        {
            public int Id { get; set; }
            public string Name { get; set; }

            [BsonIgnore]
            public string MappingPath { get; set; }
        }

        public class GenericOnlyMapper : BsonMapper
        {
            public const string Sentinel = "generic-override-result";

            public int Reads { get; private set; }
            public Type LastRequestedType { get; private set; }
            public int LastSourceId { get; private set; }

            public override T ToObject<T>(BsonDocument doc)
            {
                if (typeof(T) != typeof(GenericOnlyRow))
                {
                    return base.ToObject<T>(doc);
                }

                Reads++;
                LastRequestedType = typeof(T);
                LastSourceId = doc["_id"].AsInt32;

                var row = new GenericOnlyRow
                {
                    Id = doc["_id"].AsInt32,
                    Name = "generic:" + doc["Name"].AsString,
                    MappingPath = Sentinel
                };

                return (T)(object)row;
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

        [Theory]
        [InlineData("FindAll")]
        [InlineData("FindById")]
        [InlineData("Find")]
        [InlineData("FindOne")]
        [InlineData("Query")]
        public void Typed_read_uses_generic_only_virtual_mapper_and_returns_its_result(string api)
        {
            var mapper = new GenericOnlyMapper();
            using var db = new LiteDatabase(":memory:", mapper);
            var rawCollection = db.GetCollection("generic_rows");
            rawCollection.Insert(new BsonDocument
            {
                ["_id"] = 71,
                ["Name"] = "stored"
            });

            var collection = db.GetCollection<GenericOnlyRow>("generic_rows");
            GenericOnlyRow result;

            switch (api)
            {
                case "FindAll": result = collection.FindAll().Single(); break;
                case "FindById": result = collection.FindById(71); break;
                case "Find": result = collection.Find(x => x.Id == 71).Single(); break;
                case "FindOne": result = collection.FindOne(x => x.Id == 71); break;
                default: result = collection.Query().ToList().Single(); break;
            }

            result.Id.Should().Be(71);
            result.Name.Should().Be("generic:stored");
            result.MappingPath.Should().Be(GenericOnlyMapper.Sentinel,
                "the read must use the object returned by the generic virtual hook");
            mapper.Reads.Should().Be(1);
            mapper.LastRequestedType.Should().Be(typeof(GenericOnlyRow));
            mapper.LastSourceId.Should().Be(71);

            var stored = rawCollection.FindById(71);
            stored["Name"].AsString.Should().Be("stored");
            stored.ContainsKey(nameof(GenericOnlyRow.MappingPath)).Should().BeFalse();
        }

        [Fact]
        public void Generic_only_mapper_control_differs_from_default_mapping()
        {
            var document = new BsonDocument
            {
                ["_id"] = 83,
                ["Name"] = "control"
            };

            var defaultResult = new BsonMapper().ToObject<GenericOnlyRow>(document);
            defaultResult.Id.Should().Be(83);
            defaultResult.Name.Should().Be("control");
            defaultResult.MappingPath.Should().BeNull();

            var mapper = new GenericOnlyMapper();
            var customResult = mapper.ToObject<GenericOnlyRow>(document);
            customResult.Id.Should().Be(83);
            customResult.Name.Should().Be("generic:control");
            customResult.MappingPath.Should().Be(GenericOnlyMapper.Sentinel);
            mapper.Reads.Should().Be(1);
            mapper.LastRequestedType.Should().Be(typeof(GenericOnlyRow));
            mapper.LastSourceId.Should().Be(83);
        }
    }
}
