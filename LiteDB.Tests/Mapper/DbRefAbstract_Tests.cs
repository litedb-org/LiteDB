using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class DbRefAbstract_Tests
    {
        public class ProjectList
        {
            public int Id { get; set; }

            public string Name { get; set; }

            [BsonRef("items")]
            public List<ItemBase> Items { get; set; }
        }

        public class ProjectItem
        {
            public int Id { get; set; }

            public string Name { get; set; }

            public ItemBase Item { get; set; }
        }

        public abstract class ItemBase
        {
            public Guid Id { get; set; }

            public string Name { get; set; }
        }

        public class ItemA : ItemBase
        {
            public string DetailsA { get; set; }
        }

        public class ItemB : ItemBase
        {
            public string DetailsB { get; set; }
        }

        [Fact]
        public void DbRef_List_Using_Abstract_Class()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var projectsCollection = db.GetCollection<ProjectList>("projects");
                var itemsCollection = db.GetCollection<ItemBase>("items");

                var itemA = new ItemA { Name = "Item A1", DetailsA = "Details A1" };
                itemsCollection.Insert(itemA);
                var itemB = new ItemB { Name = "Item B1", DetailsB = "Details B1" };
                itemsCollection.Insert(itemB);

                var project = new ProjectList
                {
                    Name = "Project 1",
                    Items = new List<ItemBase> { itemA, itemB }
                };
                projectsCollection.Insert(project);

                var queryResult = projectsCollection.FindAll().First();

                // no Include - must use $type to load reference class

                queryResult.Items[0].GetType().Should().Be(typeof(ItemA));
                queryResult.Items[1].GetType().Should().Be(typeof(ItemB));
            }
        }

        [Fact]
        public void DbRef_Item_Using_Abstract_Class()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var projectsCollection = db.GetCollection<ProjectItem>("projects");
                var itemsCollection = db.GetCollection<ItemBase>("items");

                var itemA = new ItemA { Name = "Item A1", DetailsA = "Details A1" };
                itemsCollection.Insert(itemA);
                var itemB = new ItemB { Name = "Item B1", DetailsB = "Details B1" };
                itemsCollection.Insert(itemB);

                var projectA = new ProjectItem
                {
                    Name = "Project A",
                    Item = itemA
                };

                var projectB = new ProjectItem
                {
                    Name = "Project B",
                    Item = itemB
                };

                projectsCollection.Insert(projectA);
                projectsCollection.Insert(projectB);

                var queryResult = projectsCollection.FindAll().ToArray();

                // no Include - must use $type to load reference class

                queryResult[0].Item.GetType().Should().Be(typeof(ItemA));
                queryResult[1].Item.GetType().Should().Be(typeof(ItemB));
            }
        }
    }
}