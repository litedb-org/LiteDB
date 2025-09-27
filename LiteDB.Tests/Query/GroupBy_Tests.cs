using FluentAssertions;
using System.Linq;
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
                .Select(g => (Age: g.Key, Count: g.Count()))
                .OrderBy(x => x.Age)
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Age)
                .Select(g => new { Age = g.Key, Count = g.Count() })
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
                .Select(g => new { Year = g.Key, Sum = g.Sum(p => p.Age) })
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
                .Select(g => new { Age = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Age)
                .Skip(5)
                .Take(3)
                .Select(x => (x.Age, x.Count))
                .ToArray();

            var actual = collection.Query()
                .GroupBy(x => x.Age)
                .Select(g => new { Age = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Age)
                .Skip(5)
                .Limit(3)
                .ToArray()
                .Select(x => (x.Age, x.Count))
                .ToArray();

            actual.Should().Equal(expected);
        }
    }
}
