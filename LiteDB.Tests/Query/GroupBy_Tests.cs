using System.IO;
using System.Linq;

using FluentAssertions;
using LiteDB;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class GroupBy_Tests
    {
        [Fact]
        [Trait("Area", "GroupBy")]
        public void Having_Sees_Group_Key()
        {
            using var stream = new MemoryStream();
            using var db = new LiteDatabase(stream);
            var col = db.GetCollection<GroupItem>("numbers");

            for (var i = 1; i <= 5; i++)
            {
                col.Insert(new GroupItem { Id = i, Value = i, Parity = i % 2 });
            }

            var results = col.Query()
                .GroupBy(x => x.Parity)
                .Having(BsonExpression.Create("@key = 1"))
                .Select(g => new { g.Key, Count = g.Count() })
                .ToArray();

            var because = $"HAVING must see @key; got {results.Length} groups.";
            results.Should().HaveCount(1, because);
            results[0].Key.Should().Be(1, "HAVING must filter on the grouping key value 1.");
            results[0].Count.Should().Be(3, "HAVING must aggregate the odd values (1, 3, 5).");
        }

        [Fact]
        [Trait("Area", "GroupBy")]
        public void GroupBy_Respects_Collation_For_Key_Equality()
        {
            using var dataStream = new MemoryStream();
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = dataStream,
                Collation = new Collation("en-US/IgnoreCase")
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var col = db.GetCollection<NamedItem>("names");

            col.Insert(new NamedItem { Id = 1, Name = "ALICE" });
            col.Insert(new NamedItem { Id = 2, Name = "alice" });
            col.Insert(new NamedItem { Id = 3, Name = "Alice" });

            var results = col.Query()
                .GroupBy(x => x.Name)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToArray();

            var because = $"Grouping equality must honor the configured case-insensitive collation; got {results.Length} groups.";
            results.Should().HaveCount(1, because);
            results[0].Count.Should().Be(3, "Grouping equality must honor the configured case-insensitive collation.");
        }

        [Fact]
        [Trait("Area", "GroupBy")]
        public void OrderBy_Count_Before_Select_Uses_Group_Not_Projection()
        {
            using var stream = new MemoryStream();
            using var db = new LiteDatabase(stream);
            var col = db.GetCollection<CategoryItem>("items");

            var items = new[] 
            {
                new CategoryItem { Id = 1, Category = "A" },
                new CategoryItem { Id = 2, Category = "A" },
                new CategoryItem { Id = 3, Category = "A" },
                new CategoryItem { Id = 4, Category = "B" },
                new CategoryItem { Id = 5, Category = "B" },
                new CategoryItem { Id = 6, Category = "C" }
            };
            
            col.InsertBulk(items);
            
            var ascending = col.Query()
                .GroupBy(x => x.Category)
                .OrderBy(g => g.Count())
                .Select(g => new { g.Key, Size = g.Count() })
                .ToArray();
            
            var expectedAscending = items
                .GroupBy(x => x.Category)
                .OrderBy(g => g.Count())
                .Select(g => new { g.Key, Size = g.Count() })
                .ToArray();

            var ascendingSizes = ascending.Select(x => x.Size).ToArray();
            var expectedAscendingSizes = expectedAscending.Select(x => x.Size).ToArray();
            ascendingSizes.Should().Equal(expectedAscendingSizes, //new[] { 1, 2, 3 }
                "OrderBy aggregate must be evaluated over the group source before projection (ascending). Actual: {0}",
                string.Join(", ", ascendingSizes));

            var ascendingKeys = ascending.Select(x => x.Key).ToArray();
            var expectedAscendingKeys = expectedAscending.Select(x => x.Key).ToArray();
            ascendingKeys.Should().Equal(expectedAscendingKeys, //new[] { "C", "B", "A" }
                "OrderBy aggregate must order groups by their aggregated size (ascending). Actual: {0}",
                string.Join(", ", ascendingKeys));

            var descending = col.Query()
                .GroupBy(x => x.Category)
                .OrderByDescending(g => g.Count())
                .Select(g => new { g.Key, Size = g.Count() })
                .ToArray();
            
            var expectedDescending = items
                .GroupBy(x => x.Category)
                .OrderByDescending(g => g.Count())
                .Select(g => new { g.Key, Size = g.Count() })
                .ToArray();

            var descendingSizes = descending.Select(x => x.Size).ToArray();
            var expectedDescendingSizes = expectedDescending.Select(x => x.Size).ToArray();
            descendingSizes.Should().Equal(expectedDescendingSizes, // new[] { 3, 2, 1 }
                "OrderBy aggregate must be evaluated over the group source before projection (descending). Actual: {0}",
                string.Join(", ", descendingSizes));

            var descendingKeys = descending.Select(x => x.Key).ToArray();
            var expectedDescendingKeys = expectedDescending.Select(x => x.Key).ToArray();
            descendingKeys.Should().Equal(expectedDescendingKeys, //new[] { "A", "B", "C" }
                "OrderBy aggregate must order groups by their aggregated size (descending). Actual: {0}",
                string.Join(", ", descendingKeys));
        }

        [Fact]
        public void Query_GroupBy_Age_With_Count()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            var expected = local
                .GroupBy(x => x.Age)
                .Select(g => (Age: g.Key, Count: g.Count()))
                .OrderBy(x => x.Age)
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Age)
                .Select
                (
                    g => new
                    {
                        Age = g.Key,
                        Count = g.Count()
                    }
                )
                .OrderBy(x => x.Age)
                .ToArray()
                .Select(x => (x.Age, x.Count))
                .ToArray();

            actual.Should().Equal(expected);
        }

        [Fact]
        public void Query_GroupBy_Year_With_Sum_Age()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            var expected = local
                .GroupBy(x => x.Date.Year)
                .Select(g => (Year: g.Key, Sum: g.Sum(p => p.Age)))
                .OrderBy(x => x.Year)
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Date.Year)
                .Select
                (
                    g => new
                    {
                        Year = g.Key,
                        Sum = g.Sum(p => p.Age)
                    }
                )
                .OrderBy(x => x.Year)
                .ToArray()
                .Select(x => (x.Year, x.Sum))
                .ToArray();

            actual.Should().Equal(expected);
        }

        [Fact]
        public void Query_GroupBy_Order_And_Limit()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            var expected = local
                .GroupBy(x => x.Age)
                .Select
                (
                    g => new
                    {
                        Age = g.Key,
                        Count = g.Count()
                    }
                )
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Age)
                .Skip(5)
                .Take(3)
                .Select(x => (x.Age, x.Count))
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Age)
                .Select
                (
                    g => new
                    {
                        Age = g.Key,
                        Count = g.Count()
                    }
                )
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Age)
                .Skip(5)
                .Limit(3)
                .ToArray()
                .Select(x => (x.Age, x.Count))
                .ToArray();

            actual.Should().Equal(expected);
        }

        [Fact]
        public void Query_GroupBy_ToList_Materializes_Groupings()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            var expected = local
                .GroupBy(x => x.Age)
                .OrderBy(g => g.Key)
                .Select
                (
                    g => new
                    {
                        Key = g.Key,
                        Names = g.OrderBy(p => p.Name).Select(p => p.Name).ToArray()
                    }
                )
                .ToArray();

            var groupings = collection.Query()
                .GroupBy(x => x.Age)
                .ToList();

            groupings.Should().AllBeAssignableTo<IGrouping<int, Person>>();

            var actual = groupings
                .OrderBy(g => g.Key)
                .Select
                (
                    g => new
                    {
                        g.Key,
                        Names = g.OrderBy(p => p.Name).Select(p => p.Name).ToArray()
                    }
                )
                .ToArray();

            actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        }

        [Fact]
        public void Query_GroupBy_OrderBy_Key_Before_Select_Should_Work()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            // This test specifically targets the bug where OrderBy(g => g.Key) before Select()
            // causes issues with @key parameter binding in the GroupByPipe
            var expected = local
                .GroupBy(x => x.Age)
                .OrderBy(g => g.Key) // Order by key BEFORE projection
                .Select
                (
                    g => new
                    {
                        Age = g.Key,
                        Count = g.Count()
                    }
                )
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Age)
                .OrderBy(g => g.Key) // This should work but currently fails due to @key not being bound
                .Select
                (
                    g => new
                    {
                        Age = g.Key,
                        Count = g.Count()
                    }
                )
                .ToArray();

            actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        }

        [Fact]
        public void Query_GroupBy_OrderByDescending_Key_Before_Select_Should_Work()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            // Test descending order as well to ensure the fix works for both directions
            var expected = local
                .GroupBy(x => x.Age)
                .OrderByDescending(g => g.Key) // Order by key descending BEFORE projection
                .Select
                (
                    g => new
                    {
                        Age = g.Key,
                        Count = g.Count()
                    }
                )
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Age)
                .OrderByDescending(g => g.Key) // This should work but currently fails
                .Select
                (
                    g => new
                    {
                        Age = g.Key,
                        Count = g.Count()
                    }
                )
                .ToArray();

            actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        }

        [Fact]
        public void Query_GroupBy_Complex_Key_OrderBy_Before_Select_Should_Work()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            // Test with a more complex grouping key to ensure the fix works with different key types
            var expected = local
                .GroupBy(x => x.Date.Year)
                .OrderBy(g => g.Key) // Order by year key BEFORE projection
                .Select
                (
                    g => new
                    {
                        Year = g.Key,
                        TotalAge = g.Sum(p => p.Age)
                    }
                )
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Date.Year)
                .OrderBy(g => g.Key) // This should work but currently fails
                .Select
                (
                    g => new
                    {
                        Year = g.Key,
                        TotalAge = g.Sum(p => p.Age)
                    }
                )
                .ToArray();

            actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        }

        private class GroupItem
        {
            public int Id { get; set; }

            public int Value { get; set; }

            public int Parity { get; set; }
        }

        private class NamedItem
        {
            public int Id { get; set; }

            public string Name { get; set; } = string.Empty;
        }

        private class CategoryItem
        {
            public int Id { get; set; }

            public string Category { get; set; } = string.Empty;
        }

    }
}
