using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2798InheritedId_Tests
    {
        public abstract class Vehicle { public int Id { get; set; } public string Name { get; set; } }
        public class Car : Vehicle { public int Doors { get; set; } }
        public class Truck : Vehicle { public int Axles { get; set; } }

        public class Animal { public int Id { get; set; } public string Name { get; set; } }
        public class Dog : Animal { public bool Barks { get; set; } }

        public abstract class CodedDoc { [BsonId] public string Code { get; set; } public int Rank { get; set; } }
        public class CodedLeaf : CodedDoc { }

        public abstract class KeyedDoc<T> { public T Id { get; set; } public string Name { get; set; } }
        public class IntDoc : KeyedDoc<int> { }
        public class TextDoc : KeyedDoc<string> { }

        public abstract class FieldDoc { public int Id; public string Name; }
        public class FieldLeaf : FieldDoc { }

        public abstract class Part { public int Id { get; set; } public string Serial { get; set; } }
        public class PlainPart : Part { }
        public class SerialPart : Part { [BsonId] public string Tag { get; set; } }

        [Fact]
        public void Abstract_base_with_non_virtual_id_queries_derived_rows_by_id()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var col = db.GetCollection<Vehicle>("v");
            col.Insert(new Car { Id = 1, Name = "one", Doors = 4 });
            col.Insert(new Car { Id = 2, Name = "two", Doors = 2 });

            col.Find(x => x.Id == 1).Select(x => x.Name).Should().Equal("one");
            col.FindOne(x => x.Id == 2).Name.Should().Be("two");
            col.Query().OrderByDescending(x => x.Id).Select(x => x.Id).ToArray().Should().Equal(2, 1);
            col.DeleteMany(x => x.Id == 1).Should().Be(1);
            col.FindAll().Select(x => x.Id).Should().Equal(2);
        }

        [Fact]
        public void Two_derived_types_sharing_the_inherited_id_agree_on_the_id_mapping()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var col = db.GetCollection<Vehicle>("v");
            col.Insert(new Car { Id = 1, Name = "car" });
            col.Insert(new Truck { Id = 2, Name = "truck" });

            col.Find(x => x.Id == 2).Single().Should().BeOfType<Truck>();
            col.Find(x => x.Id >= 1).Select(x => x.Name).Should().BeEquivalentTo("car", "truck");
        }

        [Fact]
        public void Non_abstract_base_collection_queries_derived_rows_by_id()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var col = db.GetCollection<Animal>("a");
            col.Insert(new Dog { Id = 1, Name = "rex", Barks = true });

            col.Find(x => x.Id == 1).Single().Should().BeOfType<Dog>();
            col.DeleteMany(x => x.Id == 1).Should().Be(1);
        }

        [Fact]
        public void Abstract_base_with_attributed_id_queries_derived_rows_by_id()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var col = db.GetCollection<CodedDoc>("c");
            col.Insert(new CodedLeaf { Code = "alpha", Rank = 1 });

            db.GetCollection("c").FindById("alpha")["Rank"].AsInt32.Should().Be(1);
            col.Find(x => x.Code == "alpha").Select(x => x.Rank).Should().Equal(1);
            col.Find(x => x.Rank == 1).Select(x => x.Code).Should().Equal("alpha");
        }

        [Fact]
        public void Generic_abstract_base_resolves_the_id_per_closed_type()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var numbers = db.GetCollection<KeyedDoc<int>>("n");
            var texts = db.GetCollection<KeyedDoc<string>>("t");
            numbers.Insert(new IntDoc { Id = 7, Name = "seven" });
            texts.Insert(new TextDoc { Id = "k", Name = "text" });

            numbers.Find(x => x.Id == 7).Select(x => x.Name).Should().Equal("seven");
            texts.Find(x => x.Id == "k").Select(x => x.Name).Should().Equal("text");
        }

        [Fact]
        public void Abstract_base_with_inherited_id_field_queries_derived_rows_by_id()
        {
            var mapper = new BsonMapper { IncludeFields = true };
            using var db = new LiteDatabase(":memory:", mapper);
            var col = db.GetCollection<FieldDoc>("f");
            col.Insert(new FieldLeaf { Id = 3, Name = "three" });

            col.Find(x => x.Id == 3).Select(x => x.Name).Should().Equal("three");
        }

        [Fact]
        public void Derived_types_that_really_disagree_on_the_inherited_id_still_throw()
        {
            var mapper = new BsonMapper();
            mapper.ToDocument<Part>(new PlainPart { Id = 1 });
            mapper.GetExpression<Part, bool>(row => row.Id == 1).Source.Should().Contain("$._id");

            mapper.ToDocument<Part>(new SerialPart { Id = 2, Tag = "t" });
            Action resolve = () => mapper.GetExpression<Part, bool>(row => row.Id == 1);
            resolve.Should().Throw<NotSupportedException>().WithMessage("*disagree*_id*");
        }
    }
}
