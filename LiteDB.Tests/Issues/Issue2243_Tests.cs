using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2243_Tests
    {
        public interface ITarget { string Name { get; set; } }
        public class Target : ITarget { public string Name { get; set; } }
        public class Row
        {
            public int Id { get; set; }
            [BsonRef("targets")] public List<ITarget> Targets { get; set; }
        }

        [Fact]
        public void Reference_list_without_mapped_id_is_rejected_before_any_write()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var raw = db.GetCollection("rows");
            raw.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
            Action invalid = () => db.GetCollection<Row>("rows").Insert(new Row
            {
                Id = 2, Targets = new List<ITarget> { new Target { Name = "no id" } }
            });
            invalid.Should().Throw<LiteException>().WithMessage("*id*");
            raw.Count().Should().Be(1);
            raw.FindById(1)["value"].AsString.Should().Be("untouched");
            raw.Insert(new BsonDocument { ["_id"] = 3, ["value"] = "still usable" });
            raw.FindById(3)["value"].AsString.Should().Be("still usable");
        }
    }
}
