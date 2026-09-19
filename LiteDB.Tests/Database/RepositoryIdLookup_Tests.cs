using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class RepositoryIdLookup_Tests
    {
        [Fact]
        public void Optional_id_lookup_returns_the_match_or_null()
        {
            using ILiteRepository repository = new LiteRepository(new MemoryStream());
            repository.Insert(new Customer { Id = 7, Name = "Ada" }, "customers");

            repository.SingleOrDefaultById<Customer>(7, "customers").Name.Should().Be("Ada");
            repository.SingleOrDefaultById<Customer>(8, "customers").Should().BeNull();
            repository.Query<Customer>("customers").SingleOrDefaultById(7).Name.Should().Be("Ada");
            repository.Query<Customer>("customers").SingleOrDefaultById(8).Should().BeNull();

            Action required = () => repository.SingleById<Customer>(8, "customers");
            required.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void Query_id_lookup_preserves_an_existing_predicate()
        {
            using var repository = new LiteRepository(new MemoryStream());
            repository.Insert(new Customer { Id = 7, Name = "Ada" });

            repository.Query<Customer>().Where(x => x.Name == "Grace")
                .SingleOrDefaultById(7).Should().BeNull();
        }

        public class Customer
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }
    }
}
