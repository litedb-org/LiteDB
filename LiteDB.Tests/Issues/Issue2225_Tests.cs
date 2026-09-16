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

        public class IntegerRow : Base<int>
        {
        }

        public class HiddenReadOnlyRow : Base<Guid>
        {
            public new Guid Id => Guid.Empty;
        }

        [Fact]
        public void Inherited_private_setter_receives_generated_ids_without_making_hidden_getters_writable()
        {
            using var file = new TempFile();
            var row = new IntegerRow();
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                db.GetCollection<IntegerRow>("rows").Insert(row).AsInt32.Should().Be(1);
                row.Id.Should().Be(1);
                Action writeReadOnly = () => db.GetCollection<HiddenReadOnlyRow>("readonly").Insert(new HiddenReadOnlyRow());
                writeReadOnly.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.PROPERTY_READ_WRITE);
                db.GetCollection("readonly").Count().Should().Be(0);
            }
            using var reopened = new LiteDatabase(file.Filename, new BsonMapper());
            reopened.GetCollection<IntegerRow>("rows").FindById(1).Id.Should().Be(1);
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
