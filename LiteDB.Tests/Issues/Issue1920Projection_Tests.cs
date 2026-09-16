using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1920Projection_Tests
    {
        public abstract class Item { public int Id { get; set; } }
        public class DerivedItem : Item { public string Detail { get; set; } }
        public class PolymorphicRow
        {
            public int Id { get; set; }
            [BsonRef("items")] public Item Item { get; set; }
            [BsonRef("items")] public List<Item> Items { get; set; }
        }

        [Fact]
        public void Resolved_polymorphic_items_keep_type_metadata_in_standalone_projections()
        {
            using var db = new LiteDatabase(":memory:");
            var item = new DerivedItem { Id = 7, Detail = "derived" };
            db.GetCollection<DerivedItem>("items").Insert(item);
            Assert.False(db.GetCollection("items").FindById(7).ContainsKey("_type"));
            var rows = db.GetCollection<PolymorphicRow>("rows");
            rows.Insert(new PolymorphicRow { Id = 1, Item = item, Items = new List<Item> { item } });
            var projected = rows.Include(x => x.Item).Include(x => x.Items).Query()
                .Select(x => new { x.Item, x.Items }).Single();
            Assert.Equal(7, Assert.IsType<DerivedItem>(projected.Item).Id);
            Assert.Equal("derived", Assert.IsType<DerivedItem>(projected.Items.Single()).Detail);
        }

        public class Child
        {
            [BsonId] public string Key { get; set; }
            public string Name { get; set; }
        }
        public class Parent
        {
            public int Id { get; set; }
            [BsonRef("children")] public Child Child { get; set; }
        }
        public class Row
        {
            public int Id { get; set; }
            [BsonRef("parents")] public Parent Parent { get; set; }
            [BsonRef("children")] public List<Child> Children { get; set; }
        }

        [Fact]
        public void Nested_and_array_projections_keep_custom_ids_without_changing_stubs()
        {
            using var db = new LiteDatabase(":memory:");
            var child = new Child { Key = "child-key", Name = "payload" };
            var parent = new Parent { Id = 23, Child = child };
            db.GetCollection<Child>("children").Insert(child);
            db.GetCollection<Parent>("parents").Insert(parent);
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1, Parent = parent, Children = new List<Child> { child } });
            var included = rows.Include(x => x.Parent).Include(x => x.Parent.Child).Include(x => x.Children);
            var result = included.Query().Select(x => new { x.Parent, Nested = x.Parent.Child, x.Children }).Single();
            Assert.Equal(23, result.Parent.Id);
            Assert.Equal("child-key", result.Nested.Key);
            Assert.Equal("payload", result.Nested.Name);
            Assert.Equal("child-key", result.Children.Single().Key);
            Assert.Equal("child-key", result.Parent.Child.Key);
            Assert.Equal(23, included.Query().Select(x => x.Parent.Id).Single());

            var raw = db.GetCollection("rows").Include("$.Parent").FindById(1)["Parent"].AsDocument;
            Assert.Equal(23, raw["_id"].AsInt32);
            Assert.Equal(23, raw["$id"].AsInt32);
            Assert.False(raw.ContainsKey("$ref"));
            var stored = db.GetCollection("rows").FindById(1)["Parent"].AsDocument;
            Assert.False(stored.ContainsKey("_id"));
            Assert.Equal("parents", stored["$ref"].AsString);
        }
    }
}
