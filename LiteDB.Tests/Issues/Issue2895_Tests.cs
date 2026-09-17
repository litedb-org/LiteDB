using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2895_Tests
    {
        public class Item
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        [Theory]
        [InlineData("insert", false)]
        [InlineData("insert", true)]
        [InlineData("update", false)]
        [InlineData("update", true)]
        [InlineData("upsert", false)]
        [InlineData("upsert", true)]
        public void Repository_collection_arguments_use_the_many_entity_overload(
            string operation,
            bool useList)
        {
            using var repository = new LiteRepository(new MemoryStream());
            var values = new[]
            {
                new Item { Id = 1, Name = "first" },
                new Item { Id = 2, Name = "second" }
            };

            if (operation == "update")
            {
                repository.Insert<Item>(values);
                values[0].Name = "updated-first";
                values[1].Name = "updated-second";
            }

            if (useList)
            {
                Execute(repository, operation, values.ToList()).Should().Be(2);
            }
            else
            {
                Execute(repository, operation, values).Should().Be(2);
            }

            repository.Query<Item>().OrderBy(item => item.Id).ToList()
                .Select(item => item.Name).Should().Equal(values.Select(item => item.Name));
        }

        private static int Execute(LiteRepository repository, string operation, Item[] values)
        {
            if (operation == "insert") return repository.Insert(values);
            if (operation == "update") return repository.Update(values);
            return repository.Upsert(values);
        }

        private static int Execute(LiteRepository repository, string operation, List<Item> values)
        {
            if (operation == "insert") return repository.Insert(values);
            if (operation == "update") return repository.Update(values);
            return repository.Upsert(values);
        }

        [Fact]
        public void Interface_overloads_preserve_named_collection_and_bulk_counts()
        {
            using ILiteRepository repository = new LiteRepository(":memory:");
            var values = new[] { new Item { Id = 1 }, new Item { Id = 2 } };
            repository.Insert(values, "named").Should().Be(2);
            repository.Update(values.ToList(), "named").Should().Be(2);
            repository.Upsert(values, "named").Should().Be(0);
            repository.Upsert(new List<Item> { new Item { Id = 3 } }, "named").Should().Be(1);
            repository.Insert(new List<Item> { new Item { Id = 4 } }, "named").Should().Be(1);
            repository.Update(new[] { new Item { Id = 4 }, new Item { Id = 5 } }, "named").Should().Be(1);
            repository.Query<Item>("named").Count().Should().Be(4);
            repository.Database.GetCollectionNames().Should().Equal("named");
        }

        [Fact]
        public void Empty_batches_are_noops_and_single_entities_keep_their_return_contracts()
        {
            using var repository = new LiteRepository(":memory:");
            repository.Insert(new Item[0]).Should().Be(0);
            repository.Update(new List<Item>()).Should().Be(0);
            repository.Upsert(new Item[0]).Should().Be(0);
            BsonValue id = repository.Insert(new Item { Id = 1 });
            id.AsInt32.Should().Be(1);
            bool updated = repository.Update(new Item { Id = 1 });
            updated.Should().BeTrue();
            bool inserted = repository.Upsert(new Item { Id = 1 });
            inserted.Should().BeFalse();
        }
    }
}
