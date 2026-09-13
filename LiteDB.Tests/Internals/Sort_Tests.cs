using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class Sort_Tests
    {
        private readonly IStreamFactory _factory = new StreamFactory(new MemoryStream(), null);

        [Fact]
        public void Sort_String_Asc()
        {
            var source = Enumerable.Range(0, 2000)
                .Select(x => Guid.NewGuid().ToString())
                .Select(x => new KeyValuePair<BsonValue, PageAddress>(x, PageAddress.Empty))
                .ToArray();

            var pragmas = new EnginePragmas(null);
            pragmas.Set(Pragmas.COLLATION, Collation.Binary.ToString(), false);

            using (var tempDisk = new SortDisk(_factory, 10 * 8192, pragmas))
            using (var s = new SortService(tempDisk, Query.Ascending, pragmas))
            {
                s.Insert(source);

                s.Count.Should().Be(2000);
                s.Containers.Count.Should().Be(2);

                s.Containers.ElementAt(0).Count.Should().Be(1905);
                s.Containers.ElementAt(1).Count.Should().Be(95);

                var output = s.Sort().ToArray();

                output.Should().Equal(source.OrderBy(x => x.Key).ToArray());
            }
        }

        [Fact]
        public void Sort_Int_Desc()
        {
            var rnd = new Random();
            var source = Enumerable.Range(0, 20000)
                .Select(x => new KeyValuePair<BsonValue, PageAddress>(rnd.Next(1, 30000), PageAddress.Empty))
                .ToArray();

            var pragmas = new EnginePragmas(null);
            pragmas.Set(Pragmas.COLLATION, Collation.Binary.ToString(), false);

            using (var tempDisk = new SortDisk(_factory, 10 * 8192, pragmas))
            using (var s = new SortService(tempDisk, Query.Descending, pragmas))
            {
                s.Insert(source);

                s.Count.Should().Be(20000);
                s.Containers.Count.Should().Be(3);

                s.Containers.ElementAt(0).Count.Should().Be(8192);
                s.Containers.ElementAt(1).Count.Should().Be(8192);
                s.Containers.ElementAt(2).Count.Should().Be(3616);

                var output = s.Sort().ToArray();

                output.Should().Equal(source.OrderByDescending(x => x.Key).ToArray());
            }
        }
    }
}