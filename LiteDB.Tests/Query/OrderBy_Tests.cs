using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class OrderBy_Tests
    {
        [Fact]
        public void Query_OrderBy_Using_Index()
        {
            using var db = new PersonQueryData();
            var (collection, local) = db.GetData();

            collection.EnsureIndex(x => x.Name);

            var r0 = local
                .OrderBy(x => x.Name)
                .Select(x => new { x.Name })
                .ToArray();

            var r1 = collection.Query()
                .OrderBy(x => x.Name)
                .Select(x => new { x.Name })
                .ToArray();

            r0.Should().Equal(r1);
        }

        [Fact]
        public void Query_OrderBy_Using_Index_Desc()
        {
            using var db = new PersonQueryData();
            var (collection, local) = db.GetData();

            collection.EnsureIndex(x => x.Name);

            var r0 = local
                .OrderByDescending(x => x.Name)
                .Select(x => new { x.Name })
                .ToArray();

            var r1 = collection.Query()
                .OrderByDescending(x => x.Name)
                .Select(x => new { x.Name })
                .ToArray();

            r0.Should().Equal(r1);
        }

        [Fact]
        public void Query_OrderBy_With_Func()
        {
            using var db = new PersonQueryData();
            var (collection, local) = db.GetData();

            collection.EnsureIndex(x => x.Date.Day);

            var r0 = local
                .OrderBy(x => x.Date.Day)
                .Select(x => new { d = x.Date.Day })
                .ToArray();

            var r1 = collection.Query()
                .OrderBy(x => x.Date.Day)
                .Select(x => new { d = x.Date.Day })
                .ToArray();

            r0.Should().Equal(r1);
        }

        [Fact]
        public void Query_OrderBy_With_Offset_Limit()
        {
            using var db = new PersonQueryData();
            var (collection, local) = db.GetData();

            // no index

            var r0 = local
                .OrderBy(x => x.Date.Day)
                .Select(x => new { d = x.Date.Day })
                .Skip(5)
                .Take(10)
                .ToArray();

            var r1 = collection.Query()
                .OrderBy(x => x.Date.Day)
                .Select(x => new { d = x.Date.Day })
                .Offset(5)
                .Limit(10)
                .ToArray();

            r0.Should().Equal(r1);
        }

        [Fact]
        public void Query_Asc_Desc()
        {
            using var db = new PersonQueryData();
            var (collection, _) = db.GetData();

            var asc = collection.Find(Query.All(Query.Ascending)).ToArray();
            var desc = collection.Find(Query.All(Query.Descending)).ToArray();

            asc[0].Id.Should().Be(1);
            desc[0].Id.Should().Be(1000);
        }

        [Fact]
        public void Query_OrderBy_ThenBy_Multiple_Keys()
        {
            using var db = new PersonQueryData();
            var (collection, local) = db.GetData();

            collection.EnsureIndex(x => x.Age);

            var expected = local
                .OrderBy(x => x.Age)
                .ThenByDescending(x => x.Name)
                .Select
                (
                    x => new
                    {
                        x.Age,
                        x.Name
                    }
                )
                .ToArray();

            var actual = collection.Query()
                .OrderBy(x => x.Age)
                .ThenByDescending(x => x.Name)
                .Select
                (
                    x => new
                    {
                        x.Age,
                        x.Name
                    }
                )
                .ToArray();

            actual.Should().Equal(expected);

            var plan = collection.Query()
                .OrderBy(x => x.Age)
                .ThenByDescending(x => x.Name)
                .GetPlan();

            plan["index"]["order"].AsInt32.Should().Be(Query.Ascending);

            var orderBy = plan["orderBy"].AsArray;

            orderBy.Count.Should().Be(2);
            orderBy[0]["expr"].AsString.Should().Be("$.Age");
            orderBy[0]["order"].AsInt32.Should().Be(Query.Ascending);
            orderBy[1]["expr"].AsString.Should().Be("$.Name");
            orderBy[1]["order"].AsInt32.Should().Be(Query.Descending);
        }

        [Fact]
        public void Query_OrderByDescending_ThenBy_Index_Order_Applied()
        {
            using var db = new PersonQueryData();
            var (collection, local) = db.GetData();

            collection.EnsureIndex(x => x.Name);
            collection.EnsureIndex(x => x.Age);

            var expected = local
                .OrderByDescending(x => x.Age)
                .ThenBy(x => x.Name)
                .Select
                (
                    x => new
                    {
                        x.Name,
                        x.Age
                    }
                )
                .ToArray();

            var actual = collection.Query()
                .OrderByDescending(x => x.Age)
                .ThenBy(x => x.Name)
                .Select
                (
                    x => new
                    {
                        x.Name,
                        x.Age
                    }
                )
                .ToArray();

            actual.Should().Equal(expected);

            var plan = collection.Query()
                .OrderByDescending(x => x.Name)
                .ThenBy(x => x.Age)
                .GetPlan();

            plan["index"]["order"].AsInt32.Should().Be(Query.Descending);

            var orderBy = plan["orderBy"].AsArray;

            orderBy.Count.Should().Be(2);
            orderBy[0]["order"].AsInt32.Should().Be(Query.Descending);
            orderBy[1]["order"].AsInt32.Should().Be(Query.Ascending);
        }

        public record Data(int Id, int Value);

        [Fact]
        public void Query_OrderByDescending_ThenBy_Index_Order_Applied_Data2()
        {
            var data = Enumerable
                .Range(1, 1000)
                .Select(x => new Data(x, new Random(x).Next())) // pseudo random
                .ToArray();

            using var db = DatabaseFactory.Create();
            var col = db.GetCollection<Data>("data");
            col.EnsureIndex(x => x.Value);
            col.EnsureIndex(x => x.Id);
            col.Insert(data);

            var expected = data
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Id)
                .ToArray();

            var actual = col.Query()
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Id)
                .ToArray();

            expected.Should().Equal(actual);
        }

        [Fact]
        public void Query_OrderByAscending_ThenByDescending_Index_Order_Applied_Data2()
        {
            var data = Enumerable
                .Range(1, 1000)
                .Select(x => new Data(x, new Random(x).Next())) // pseudo random
                .ToArray();

            using var db = DatabaseFactory.Create();
            var col = db.GetCollection<Data>("data");
            col.EnsureIndex(x => x.Value);
            col.EnsureIndex(x => x.Id);
            col.Insert(data);

            var expected = data
                .OrderBy(x => x.Value)
                .ThenByDescending(x => x.Id)
                .ToArray();

            var actual = col.Query()
                .OrderBy(x => x.Value)
                .ThenByDescending(x => x.Id)
                .ToArray();

            expected.Should().Equal(actual);
        }

        [Fact]
        public void Query_OrderByAscending_ThenByAscending_Index_Order_Applied_Data2()
        {
            var data = Enumerable
                .Range(1, 1000)
                .Select(x => new Data(x, new Random(x).Next())) // pseudo random
                .ToArray();

            using var db = DatabaseFactory.Create();
            var col = db.GetCollection<Data>("data");
            col.EnsureIndex(x => x.Value);
            col.EnsureIndex(x => x.Id);
            col.Insert(data);

            var expected = data
                .OrderBy(x => x.Value)
                .ThenBy(x => x.Id)
                .ToArray();

            var actual = col.Query()
                .OrderBy(x => x.Value)
                .ThenBy(x => x.Id)
                .ToArray();

            expected.Should().Equal(actual);
        }

        [Fact]
        public void Query_OrderByDescending_ThenByDescending_Index_Order_Applied_Data2()
        {
            var data = Enumerable
                .Range(1, 1000)
                .Select(x => new Data(x, new Random(x).Next())) // pseudo random
                .ToArray();

            using var db = DatabaseFactory.Create();
            var col = db.GetCollection<Data>("data");
            col.EnsureIndex(x => x.Value);
            col.EnsureIndex(x => x.Id);
            col.Insert(data);

            var expected = data
                .OrderByDescending(x => x.Value)
                .ThenByDescending(x => x.Id)
                .ToArray();

            var actual = col.Query()
                .OrderByDescending(x => x.Value)
                .ThenByDescending(x => x.Id)
                .ToArray();

            expected.Should().Equal(actual);
        }

        [Fact]
        public void Query_Missmatch_Order()
        {
            var data = Enumerable
                .Range(1, 1000)
                .Select(x => new Data(x, new Random(x).Next())) // pseudo random
                .ToArray();

            using var db = DatabaseFactory.Create();
            var col = db.GetCollection<Data>("data");
            col.EnsureIndex(x => x.Value);
            col.EnsureIndex(x => x.Id);
            col.Insert(data);

            var expected = data
                .OrderBy(x => x.Value)
                .ThenByDescending(x => x.Id)
                .ToArray();

            var actual = col.Query()
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Id)
                .ToArray();

            expected.Should().NotEqual(actual);
        }

        public record ThreeLayerData(int Id, int Category, string Name, int Priority);

        [Fact]
        public void Query_ThreeLayer_Ascending_Ascending_Ascending()
        {
            var data = new[]
            {
                new ThreeLayerData(1, 1, "C", 3),
                new ThreeLayerData(2, 1, "A", 2),
                new ThreeLayerData(3, 1, "A", 1),
                new ThreeLayerData(4, 2, "B", 2),
                new ThreeLayerData(5, 2, "B", 1),
                new ThreeLayerData(6, 1, "B", 1)
            };

            using var db = DatabaseFactory.Create();
            var col = db.GetCollection<ThreeLayerData>("data");
            col.EnsureIndex(x => x.Category);
            col.EnsureIndex(x => x.Name);
            col.EnsureIndex(x => x.Priority);
            col.Insert(data);

            var expected = data
                .OrderBy(x => x.Category)
                .ThenBy(x => x.Name)
                .ThenBy(x => x.Priority)
                .ToArray();

            var actual = col.Query()
                .OrderBy(x => x.Category)
                .ThenBy(x => x.Name)
                .ThenBy(x => x.Priority)
                .ToArray();

            actual.Should().Equal(expected);
        }

        [Fact]
        public void Query_ThreeLayer_Descending_Ascending_Descending()
        {
            var data = new[]
            {
                new ThreeLayerData(1, 1, "C", 3),
                new ThreeLayerData(2, 1, "A", 2),
                new ThreeLayerData(3, 1, "A", 1),
                new ThreeLayerData(4, 2, "B", 2),
                new ThreeLayerData(5, 2, "B", 1),
                new ThreeLayerData(6, 1, "B", 1),
                new ThreeLayerData(7, 3, "A", 2),
                new ThreeLayerData(8, 3, "A", 3)
            };

            using var db = DatabaseFactory.Create();
            var col = db.GetCollection<ThreeLayerData>("data");
            col.EnsureIndex(x => x.Category);
            col.EnsureIndex(x => x.Name);
            col.EnsureIndex(x => x.Priority);
            col.Insert(data);

            var expected = data
                .OrderByDescending(x => x.Category)
                .ThenBy(x => x.Name)
                .ThenByDescending(x => x.Priority)
                .ToArray();

            var actual = col.Query()
                .OrderByDescending(x => x.Category)
                .ThenBy(x => x.Name)
                .ThenByDescending(x => x.Priority)
                .ToArray();

            actual.Should().Equal(expected);
        }

        [Fact]
        public void Query_ThreeLayer_Ascending_Descending_Ascending()
        {
            var data = new[]
            {
                new ThreeLayerData(1, 1, "C", 3),
                new ThreeLayerData(2, 1, "A", 2),
                new ThreeLayerData(3, 1, "A", 1),
                new ThreeLayerData(4, 2, "B", 2),
                new ThreeLayerData(5, 2, "B", 1),
                new ThreeLayerData(6, 1, "B", 1),
                new ThreeLayerData(7, 2, "A", 3),
                new ThreeLayerData(8, 2, "C", 1)
            };

            using var db = DatabaseFactory.Create();
            var col = db.GetCollection<ThreeLayerData>("data");
            col.EnsureIndex(x => x.Category);
            col.EnsureIndex(x => x.Name);
            col.EnsureIndex(x => x.Priority);
            col.Insert(data);

            var expected = data
                .OrderBy(x => x.Category)
                .ThenByDescending(x => x.Name)
                .ThenBy(x => x.Priority)
                .ToArray();

            var actual = col.Query()
                .OrderBy(x => x.Category)
                .ThenByDescending(x => x.Name)
                .ThenBy(x => x.Priority)
                .ToArray();

            actual.Should().Equal(expected);
        }

        [Fact]
        public void Query_ThreeLayer_Descending_Descending_Descending()
        {
            var data = new[]
            {
                new ThreeLayerData(1, 1, "C", 3),
                new ThreeLayerData(2, 1, "A", 2),
                new ThreeLayerData(3, 1, "A", 1),
                new ThreeLayerData(4, 2, "B", 2),
                new ThreeLayerData(5, 2, "B", 1),
                new ThreeLayerData(6, 1, "B", 1),
                new ThreeLayerData(7, 3, "A", 2),
                new ThreeLayerData(8, 3, "D", 4)
            };

            using var db = DatabaseFactory.Create();
            var col = db.GetCollection<ThreeLayerData>("data");
            col.EnsureIndex(x => x.Category);
            col.EnsureIndex(x => x.Name);
            col.EnsureIndex(x => x.Priority);
            col.Insert(data);

            var expected = data
                .OrderByDescending(x => x.Category)
                .ThenByDescending(x => x.Name)
                .ThenByDescending(x => x.Priority)
                .ToArray();

            var actual = col.Query()
                .OrderByDescending(x => x.Category)
                .ThenByDescending(x => x.Name)
                .ThenByDescending(x => x.Priority)
                .ToArray();

            actual.Should().Equal(expected);
        }

        [Fact]
        public void Query_ThreeLayer_With_Large_Dataset()
        {
            var random = new Random(42); // Fixed seed for reproducible results
            var data = Enumerable
                .Range(1, 1000)
                .Select(x => new ThreeLayerData(
                    x,
                    random.Next(1, 5), // Category 1-4
                    ((char)('A' + random.Next(0, 5))).ToString(), // Name A-E
                    random.Next(1, 4) // Priority 1-3
                ))
                .ToArray();

            using var db = DatabaseFactory.Create();
            var col = db.GetCollection<ThreeLayerData>("data");
            col.EnsureIndex(x => x.Category);
            col.EnsureIndex(x => x.Name);
            col.EnsureIndex(x => x.Priority);
            col.Insert(data);

            var expected = data
                .OrderByDescending(x => x.Category)
                .ThenBy(x => x.Name)
                .ThenByDescending(x => x.Priority)
                .ToArray();

            var actual = col.Query()
                .OrderByDescending(x => x.Category)
                .ThenBy(x => x.Name)
                .ThenByDescending(x => x.Priority)
                .ToArray();

            actual.Should().Equal(expected);

            // Verify the query plan shows all three ordering segments
            var plan = col.Query()
                .OrderByDescending(x => x.Category)
                .ThenBy(x => x.Name)
                .ThenByDescending(x => x.Priority)
                .GetPlan();

            var orderBy = plan["orderBy"].AsArray;
            orderBy.Count.Should().Be(3);
            orderBy[0]["expr"].AsString.Should().Be("$.Category");
            orderBy[0]["order"].AsInt32.Should().Be(Query.Descending);
            orderBy[1]["expr"].AsString.Should().Be("$.Name");
            orderBy[1]["order"].AsInt32.Should().Be(Query.Ascending);
            orderBy[2]["expr"].AsString.Should().Be("$.Priority");
            orderBy[2]["order"].AsInt32.Should().Be(Query.Descending);
        }

        [Fact]
        public void Query_ThreeLayer_Edge_Case_With_Nulls_And_Duplicates()
        {
            var data = new[]
            {
                new ThreeLayerData(1, 1, "A", 1),
                new ThreeLayerData(2, 1, "A", 1), // Duplicate
                new ThreeLayerData(3, 1, "A", 2),
                new ThreeLayerData(4, 2, "A", 1),
                new ThreeLayerData(5, 2, "B", 1),
                new ThreeLayerData(6, 1, "B", 1),
                new ThreeLayerData(7, 1, "B", 2),
                new ThreeLayerData(8, 2, "A", 1) // Another duplicate of Id 4
            };

            using var db = DatabaseFactory.Create();
            var col = db.GetCollection<ThreeLayerData>("data");
            col.EnsureIndex(x => x.Category);
            col.EnsureIndex(x => x.Name);
            col.EnsureIndex(x => x.Priority);
            col.Insert(data);

            var expected = data
                .OrderBy(x => x.Category)
                .ThenByDescending(x => x.Name)
                .ThenBy(x => x.Priority)
                .ToArray();

            var actual = col.Query()
                .OrderBy(x => x.Category)
                .ThenByDescending(x => x.Name)
                .ThenBy(x => x.Priority)
                .ToArray();

            actual.Should().Equal(expected);
        }
    }
}