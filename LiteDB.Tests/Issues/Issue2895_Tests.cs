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
        [Trait("Category", "PendingBug")]
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
                Execute(repository, operation, values.ToList());
            }
            else
            {
                Execute(repository, operation, values);
            }

            repository.Query<Item>().OrderBy(item => item.Id).ToList()
                .Select(item => item.Name).Should().Equal(values.Select(item => item.Name));
        }

        private static void Execute(LiteRepository repository, string operation, Item[] values)
        {
            if (operation == "insert") repository.Insert(values);
            else if (operation == "update") repository.Update(values);
            else repository.Upsert(values);
        }

        private static void Execute(LiteRepository repository, string operation, List<Item> values)
        {
            if (operation == "insert") repository.Insert(values);
            else if (operation == "update") repository.Update(values);
            else repository.Upsert(values);
        }
    }
}
