using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2033_Tests
    {
        public class Child { public string Name { get; set; } }
        public class Row
        {
            public int Id { get; set; }
            public Child[] Children { get; set; }
        }

        [Fact]
        public void Array_projection_preserves_boundaries_order_duplicates_and_empty_arrays()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>("rows");
            col.Insert(new Row { Id = 1, Children = new[] { new Child { Name = "b" }, new Child { Name = "a" }, new Child { Name = "b" } } });
            col.Insert(new Row { Id = 2, Children = new Child[0] });
            col.Insert(new Row { Id = 3, Children = new[] { new Child { Name = "c" } } });
            var projected = col.Query().OrderBy(x => x.Id).Select(x => x.Children).ToArray();
            projected.Should().HaveCount(3);
            projected[0].Select(x => x.Name).Should().Equal("b", "a", "b");
            projected[1].Should().BeEmpty();
            projected[2].Select(x => x.Name).Should().Equal("c");
            db.GetCollection("rows").FindById(1)["Children"].AsArray
                .Select(x => x["Name"].AsString).Should().Equal("b", "a", "b");
        }
    }
}
