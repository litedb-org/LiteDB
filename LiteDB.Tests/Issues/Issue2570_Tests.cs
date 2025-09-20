using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2570_Tests
{
    public class Person
    {
        public int Id { get; set; }

        public (string FirstName, string LastName) Name { get; set; }
    }

    [Fact]
    public void Issue2570_Tuples()
    {
        using (var db = new LiteDatabase(":memory:"))
        {
            var col = db.GetCollection<Person>("Person");

            col.Insert(new Person { Name = ("John", "Doe") });
            col.Insert(new Person { Name = ("Joana", "Doe") });
            
            var result = col.FindOne(x => x.Name.FirstName == "John");

            result.Should().NotBeNull();
            result.Name.FirstName.Should().Be("John");
            result.Name.LastName.Should().Be("Doe");
        }
    }
    
    public struct PersonData
    {
        public string FirstName;
        public string LastName;
    }
    
    public class PersonWithStruct
    {
        public int Id { get; set; }

        public PersonData Name { get; set; }
    }
    
    [Fact]
    public void Issue2570_Structs()
    {
        using (var db = new LiteDatabase(":memory:"))
        {
            var col = db.GetCollection<PersonWithStruct>("Person");

            col.Insert(new PersonWithStruct { Name = new PersonData { FirstName = "John", LastName = "Doe" } });
            col.Insert(new PersonWithStruct { Name = new PersonData { FirstName = "Joana", LastName = "Doe" } });
            
            var result = col.FindOne(x => x.Name.FirstName == "John");

            result.Should().NotBeNull();
            result.Name.FirstName.Should().Be("John");
            result.Name.LastName.Should().Be("Doe");
        }
    }
}