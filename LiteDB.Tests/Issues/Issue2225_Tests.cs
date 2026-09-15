using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2225_Tests
    {
        public class Base<T>
        {
            public T Id { get; private set; }
            public void SetId(T id) { Id = id; }
        }
        public class Row : Base<Guid>
        {
            public int Value { get; set; }
            public Row() { }
            public Row(Guid id, int value) { SetId(id); Value = value; }
        }
        public class ConstructorOnly : Base<Guid>
        {
            public int Value { get; set; }
            public ConstructorOnly(Guid id, int value) { SetId(id); Value = value; }
        }

        [Fact]
        public void Inherited_private_id_setter_preserves_identity_with_parameterless_constructor()
        {
            var id = Guid.Parse("85ddfae2-67ca-4117-9f9a-7527d16972c0");
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                db.GetCollection<Row>("rows").Insert(new Row(id, 42));
                db.GetCollection<ConstructorOnly>("control").Insert(new ConstructorOnly(id, 17));
            }
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                db.GetCollection<ConstructorOnly>("control").FindById(id).Id.Should().Be(id);
                db.GetCollection("rows").FindById(id)["Value"].AsInt32.Should().Be(42);
                var col = db.GetCollection<Row>("rows");
                var loaded = col.FindAll().Single();
                loaded.Id.Should().Be(id);
                loaded.Value.Should().Be(42);
                loaded.Value = 43;
                col.Update(loaded).Should().BeTrue();
                col.FindById(id).Value.Should().Be(43);
                col.Count().Should().Be(1);
            }
        }
    }
}
