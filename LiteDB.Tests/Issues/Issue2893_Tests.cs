using System.Collections.Generic;
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
        [Trait("Category", "PendingBug")]
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
    }
}
