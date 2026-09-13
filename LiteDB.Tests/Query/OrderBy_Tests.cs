using System.Linq;
using FluentAssertions;
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
                .Select(x => new {x.Name})
                .ToArray();

            var r1 = collection.Query()
                .OrderBy(x => x.Name)
                .Select(x => new {x.Name})
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
                .Select(x => new {x.Name})
                .ToArray();

            var r1 = collection.Query()
                .OrderByDescending(x => x.Name)
                .Select(x => new {x.Name})
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
                .Select(x => new {d = x.Date.Day})
                .ToArray();

            var r1 = collection.Query()
                .OrderBy(x => x.Date.Day)
                .Select(x => new {d = x.Date.Day})
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
    }
}