using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2893_Tests
    {
        public interface IBase
        {
            int Id { get; set; }
        }

        public interface IChild : IBase
        {
            string Name { get; set; }
        }

        public class Child : IChild
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class Owner
        {
            public int Id { get; set; }
            public List<IChild> Items { get; set; }
        }

        [Fact]
        public void DbRef_list_maps_an_id_declared_on_a_base_interface()
        {
            var mapper = new BsonMapper();
            mapper.Entity<Owner>().DbRef(owner => owner.Items, "children");
            var owner = new Owner
            {
                Id = 1,
                Items = new List<IChild> { new Child { Id = 5, Name = "child" } }
            };

            var document = mapper.ToDocument(owner);

            Assert.Single(document["Items"].AsArray);
            document["Items"].AsArray[0]["$id"].AsInt32.Should().Be(5);
            document["Items"].AsArray[0]["$ref"].AsString.Should().Be("children");
        }

        public interface ILeft : IBase { }
        public interface IRight : IBase { }
        public interface IDiamond : ILeft, IRight { string Name { get; set; } }
        public class Diamond : IDiamond
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }
        public interface IHidden : IBase
        {
            [BsonId]
            new string Id { get; set; }
        }
        public class Hidden : IHidden
        {
            public string Id { get; set; }
            int IBase.Id { get; set; }
        }
        public interface INumber
        {
            [BsonField("number")]
            int Value { get; }
        }
        public interface IText
        {
            [BsonField("text")]
            string Value { get; }
        }
        public interface ICombined : INumber, IText { }
        public class Combined : ICombined
        {
            int INumber.Value => 42;
            string IText.Value => "text";
        }

        [Fact]
        public void Unrelated_interface_members_with_distinct_field_names_keep_both_accessors()
        {
            var mapper = new BsonMapper();
            var entity = mapper.GetEntityMapper(typeof(ICombined));
            entity.Members.Should().HaveCount(2);
            var value = new Combined();
            entity.Members.Single(member => member.FieldName == "number").Getter(value).Should().Be(42);
            entity.Members.Single(member => member.FieldName == "text").Getter(value).Should().Be("text");
            var document = new BsonDocument { ["number"] = 42, ["text"] = "text" };
            mapper.GetExpression<ICombined, bool>(item => ((INumber)item).Value == 42)
                .ExecuteScalar(document).AsBoolean.Should().BeTrue();
            mapper.GetExpression<ICombined, bool>(item => ((IText)item).Value == "text")
                .ExecuteScalar(document).AsBoolean.Should().BeTrue();
        }

        [Fact]
        public void Fluent_mapping_targets_the_declaring_sibling_and_does_not_fall_back_after_ignore()
        {
            var mapper = new BsonMapper();
            mapper.Entity<ICombined>().Field(item => ((IText)item).Value, "renamed");
            var document = new BsonDocument { ["number"] = 42, ["renamed"] = "text" };
            mapper.GetExpression<ICombined, bool>(item => ((IText)item).Value == "text")
                .ExecuteScalar(document).AsBoolean.Should().BeTrue();
            mapper.GetExpression<ICombined, bool>(item => ((INumber)item).Value == 42)
                .ExecuteScalar(document).AsBoolean.Should().BeTrue();
            mapper.Entity<ICombined>().Ignore(item => ((IText)item).Value);
            System.Action ignoredQuery = () => mapper.GetExpression<ICombined, bool>(item => ((IText)item).Value == "text");
            ignoredQuery.Should().Throw<System.NotSupportedException>();
            mapper.GetEntityMapper(typeof(ICombined)).Members.Single().FieldName.Should().Be("number");
        }

        [Fact]
        public void Inherited_interface_reference_can_be_inserted_included_and_queried_by_id()
        {
            var mapper = new BsonMapper();
            mapper.Entity<Owner>().DbRef(owner => owner.Items, "children");
            using var db = new LiteDatabase(":memory:", mapper);
            var child = new Child { Id = 5, Name = "child" };
            db.GetCollection<IChild>("children").Insert(child);
            var owners = db.GetCollection<Owner>();
            owners.Insert(new Owner { Id = 1, Items = new List<IChild> { child } });
            var loaded = owners.Include(owner => owner.Items).FindById(1);
            loaded.Items.Single().Id.Should().Be(5);
            loaded.Items.Single().Name.Should().Be("child");
            db.GetCollection<IChild>("children").Find(item => item.Id == 5).Single().Name.Should().Be("child");
        }

        [Fact]
        public void Diamond_interface_collects_a_common_base_id_once()
        {
            var mapper = new BsonMapper();
            var entity = mapper.GetEntityMapper(typeof(IDiamond));
            entity.Members.Count(member => member.MemberName == "Id").Should().Be(1);
            entity.Id.Getter(new Diamond { Id = 7 }).Should().Be(7);
            mapper.GetExpression<IDiamond, bool>(item => item.Id == 7).Source.Should().Contain("$._id");
        }

        [Fact]
        public void Derived_interface_redeclaration_takes_precedence_over_base_property()
        {
            var entity = new BsonMapper().GetEntityMapper(typeof(IHidden));
            entity.Members.Count(member => member.MemberName == "Id").Should().Be(1);
            entity.Id.DataType.Should().Be(typeof(string));
            entity.Id.Getter(new Hidden { Id = "derived" }).Should().Be("derived");
        }
    }
}
