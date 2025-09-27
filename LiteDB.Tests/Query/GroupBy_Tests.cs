using System;
using System.Linq;
using FluentAssertions;
using LiteDB;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class GroupBy_Tests
    {
        [Fact]
        public void Query_GroupBy_Age_With_Count()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            var expected = local
                .GroupBy(x => x.Age)
                .Select(g => new { Age = g.Key, Count = g.Count() })
                .OrderBy(x => x.Age)
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Age)
                .Select(g => new { Age = g.Key, Count = g.Count() })
                .OrderBy(x => x.Age)
                .ToArray();

            actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        }

        [Fact]
        public void Query_GroupBy_Year_With_Sum_And_Max()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            var expected = local
                .GroupBy(x => x.Date.Year)
                .Select(g => new { Year = g.Key, Sum = g.Sum(p => p.Age), Max = g.Max(p => p.Age) })
                .OrderBy(x => x.Year)
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Date.Year)
                .Select(g => new { Year = g.Key, Sum = g.Sum(p => p.Age), Max = g.Max(p => p.Age) })
                .OrderBy(x => x.Year)
                .ToArray();

            actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        }

        [Fact]
        public void Query_GroupBy_Supports_OrderBy_ThenBy_And_Limit()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            var expected = local
                .GroupBy(x => x.Date.Year)
                .Select(g => new { Year = g.Key, MaxAge = g.Max(p => p.Age) })
                .OrderByDescending(x => x.MaxAge)
                .ThenBy(x => x.Year)
                .Skip(5)
                .Take(3)
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Date.Year)
                .Select(g => new { Year = g.Key, MaxAge = g.Max(p => p.Age) })
                .OrderByDescending(x => x.MaxAge)
                .ThenBy(x => x.Year)
                .Skip(5)
                .Limit(3)
                .ToArray();

            actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        }

        [Fact]
        public void Query_GroupBy_OrderBy_Key_Before_Select()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            var expected = local
                .GroupBy(x => x.Age)
                .OrderBy(g => g.Key)
                .Select(g => new { Age = g.Key, Count = g.Count() })
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Age)
                .OrderBy(g => g.Key)
                .Select(g => new { Age = g.Key, Count = g.Count() })
                .ToArray();

            actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        }

        [Fact]
        public void Query_GroupBy_OrderBy_Count_Before_Select()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            var expected = local
                .GroupBy(x => x.Age)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .Select(g => new { Age = g.Key, Count = g.Count() })
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Age)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .Select(g => new { Age = g.Key, Count = g.Count() })
                .ToArray();

            actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        }

        [Fact]
        public void Query_GroupBy_ToList_Returns_Groupings()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            var expected = local
                .GroupBy(x => x.Age)
                .OrderBy(g => g.Key)
                .ToList();

            var actual = collection.Query()
                .GroupBy(x => x.Age)
                .OrderBy(g => g.Key)
                .ToList();

            actual.Select(g => g.Key).Should().Equal(expected.Select(g => g.Key));

            for (var i = 0; i < expected.Count; i++)
            {
                actual[i].Should().BeEquivalentTo(expected[i], options => options.WithStrictOrdering());
            }
        }

        [Fact]
        public void Query_GroupBy_With_Array_Aggregation()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            var expected = local
                .GroupBy(x => x.Email.Substring(x.Email.IndexOf("@") + 1))
                .Select(g => new
                {
                    Domain = g.Key,
                    Users = g.Select(p => new
                    {
                        Login = p.Email.Substring(0, p.Email.IndexOf("@")).ToLower(),
                        p.Name,
                        p.Age
                    }).ToArray()
                })
                .OrderBy(x => x.Domain)
                .Take(5)
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Email.Substring(x.Email.IndexOf("@") + 1))
                .Select(g => new
                {
                    Domain = g.Key,
                    Users = g.Select(p => new
                    {
                        Login = p.Email.Substring(0, p.Email.IndexOf("@")).ToLower(),
                        p.Name,
                        p.Age
                    }).ToArray()
                })
                .OrderBy(x => x.Domain)
                .Limit(5)
                .ToArray();

            actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        }

        [Fact]
        public void Query_GroupBy_With_Having_Filter()
        {
            using var db = new PersonGroupByData();
            var (collection, local) = db.GetData();

            var expected = local
                .GroupBy(x => x.Date.Year)
                .Where(g => g.Count() >= 10)
                .Select(g => new { Year = g.Key, Count = g.Count() })
                .OrderBy(x => x.Year)
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Date.Year)
                .Having(BsonExpression.Create("COUNT(@group) >= 10"))
                .Select(g => new { Year = g.Key, Count = g.Count() })
                .OrderBy(x => x.Year)
                .ToArray();

            actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        }
    }
}
