using System.Collections.Generic;
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

        public class ListRow
        {
            public int Id { get; set; }
            public List<Child> Children { get; set; }
        }

        public class EnumerableRow
        {
            public int Id { get; set; }
            public IEnumerable<Child> Children { get; set; }
        }

        [Fact]
        public void Array_projection_preserves_boundaries_order_duplicates_and_empty_arrays()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<Row>("rows");
            col.Insert(new Row { Id = 1, Children = new[] { new Child { Name = "b" }, new Child { Name = "a" }, new Child { Name = "b" } } });
            col.Insert(new Row { Id = 2, Children = new Child[0] });
            col.Insert(new Row { Id = 3, Children = new[] { new Child { Name = "c" } } });
            col.Query().OrderBy(x => x.Id).Select(x => x.Children).First()
                .Select(x => x.Name).Should().Equal("b", "a", "b");
            var projected = col.Query().OrderBy(x => x.Id).Select(x => x.Children).ToArray();
            projected.Should().HaveCount(3);
            projected[0].Select(x => x.Name).Should().Equal("b", "a", "b");
            projected[1].Should().BeEmpty();
            projected[2].Select(x => x.Name).Should().Equal("c");
            db.GetCollection("rows").FindById(1)["Children"].AsArray
                .Select(x => x["Name"].AsString).Should().Equal("b", "a", "b");
        }

        [Fact]
        public void List_projection_preserves_boundaries_order_duplicates_and_First_terminal()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<ListRow>("list_rows");
            col.Insert(new ListRow
            {
                Id = 1,
                Children = new List<Child>
                {
                    new Child { Name = "left" },
                    new Child { Name = "right" },
                    new Child { Name = "left" }
                }
            });
            col.Insert(new ListRow { Id = 2, Children = new List<Child>() });

            col.Query().OrderBy(x => x.Id).Select(x => x.Children).First()
                .Select(x => x.Name).Should().Equal("left", "right", "left");
            var projected = col.Query().OrderBy(x => x.Id).Select(x => x.Children).ToArray();
            projected.Should().HaveCount(2);
            projected[0].Select(x => x.Name).Should().Equal("left", "right", "left");
            projected[1].Should().BeEmpty();
            db.GetCollection("list_rows").FindById(1)["Children"].AsArray
                .Select(x => x["Name"].AsString).Should().Equal("left", "right", "left");
        }

        [Fact]
        public void Enumerable_projection_preserves_all_elements_and_First_terminal()
        {
            using var db = new LiteDatabase(":memory:");
            var col = db.GetCollection<EnumerableRow>("enumerable_rows");
            col.Insert(new EnumerableRow
            {
                Id = 1,
                Children = new[] { new Child { Name = "one" }, new Child { Name = "two" } }
            });
            col.Insert(new EnumerableRow { Id = 2, Children = new Child[0] });

            col.Query().OrderBy(x => x.Id).Select(x => x.Children).First()
                .Select(x => x.Name).Should().Equal("one", "two");
            var projected = col.Query().OrderBy(x => x.Id).Select(x => x.Children).ToArray();
            projected.Should().HaveCount(2);
            projected[0].Select(x => x.Name).Should().Equal("one", "two");
            projected[1].Should().BeEmpty();
            db.GetCollection("enumerable_rows").FindById(1)["Children"].AsArray
                .Select(x => x["Name"].AsString).Should().Equal("one", "two");
        }
    }
}
