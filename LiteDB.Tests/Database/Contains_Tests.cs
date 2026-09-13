using LiteDB;
using FluentAssertions;
using Xunit;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System;

namespace LiteDB.Tests.Database
{
    public class Contains_Tests
    {
        [Fact]
        public void ArrayContains_ShouldHaveCount1()
        {
            var random = new Random();
            var randomValue = random.Next();
            
            using(var database = new LiteDatabase(new MemoryStream()))
            {
                var collection = database.GetCollection<ItemWithEnumerable>();
                collection.Insert(new ItemWithEnumerable
                {
                    Array = new int[] { randomValue }
                });

                var result = collection.Find(i => i.Array.Contains(randomValue)).ToList();
                result.Should().HaveCount(1);
            }
        }

        [Fact]
        public void EnumerableAssignedArrayContains_ShouldHaveCount1()
        {
            var random = new Random();
            var randomValue = random.Next();
            
            using(var database = new LiteDatabase(new MemoryStream()))
            {
                var collection = database.GetCollection<ItemWithEnumerable>();
                collection.Insert(new ItemWithEnumerable
                {
                    Enumerable = new int[] { randomValue }
                });

                var result = collection.Find(i => i.Enumerable.Contains(randomValue)).ToList();
                result.Should().HaveCount(1);
            }
        }

        [Fact]
        public void EnumerableAssignedListContains_ShouldHaveCount1()
        {
            var random = new Random();
            var randomValue = random.Next();

            using(var database = new LiteDatabase(new MemoryStream()))
            {
                var collection = database.GetCollection<ItemWithEnumerable>();
                collection.Insert(new ItemWithEnumerable
                {
                    Enumerable = new List<int> { randomValue }
                });

                var result = collection.Find(i => i.Enumerable.Contains(randomValue)).ToList();
                result.Should().HaveCount(1);
            }
        }

        [Fact]
        public void ListContains_ShouldHaveCount1()
        {
            var random = new Random();
            var randomValue = random.Next();
            
            using(var database = new LiteDatabase(new MemoryStream()))
            {
                var collection = database.GetCollection<ItemWithEnumerable>();
                collection.Insert(new ItemWithEnumerable
                {
                    List = new List<int> { randomValue }
                });

                var result = collection.Find(i => i.List.Contains(randomValue)).ToList();
                result.Should().HaveCount(1);
            }
        }

        public class ItemWithEnumerable
        {
            public int[] Array { get; set; }
            public IEnumerable<int> Enumerable { get; set; }
            public IList<int> List { get; set; }
        }
    }
}