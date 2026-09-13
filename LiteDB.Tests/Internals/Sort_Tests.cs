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
        [Fact]
        public void Dispose_ReturnsEachStoragePositionOnlyOnce()
        {
            var pragmas = new EnginePragmas(null);
            using var disk = new SortDisk(new StreamFactory(new MemoryStream(), null, true), Constants.PAGE_SIZE, pragmas);
            using var sorter = new SortService(disk, new[] { Query.Ascending }, pragmas);
            sorter.Insert(Enumerable.Range(0, 2000)
                .Select(i => new KeyValuePair<BsonValue, PageAddress>(i, new PageAddress(1, 0))));
            var count = sorter.Containers.Count;
            count.Should().BeGreaterThan(1);
            sorter.Dispose();
            sorter.Dispose();
            var positions = Enumerable.Range(0, count * 2).Select(_ => disk.GetContainerPosition()).ToArray();
            positions.Should().OnlyHaveUniqueItems();
        }

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
            using (var s = new SortService(tempDisk, new[] { Query.Ascending }, pragmas))
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
            var source = Enumerable.Range(0, 900)
                .Select(x => (x * 37) % 1000)
                .Select(x => new KeyValuePair<BsonValue, PageAddress>(x, PageAddress.Empty))
                .ToArray();

            var pragmas = new EnginePragmas(null);
            pragmas.Set(Pragmas.COLLATION, Collation.Binary.ToString(), false);

            using (var tempDisk = new SortDisk(_factory, 8192, pragmas))
            using (var s = new SortService(tempDisk, [Query.Descending], pragmas))
            {
                s.Insert(source);

                s.Count.Should().Be(900);
                s.Containers.Count.Should().Be(2);

                s.Containers.ElementAt(0).Count.Should().Be(819);
                s.Containers.ElementAt(1).Count.Should().Be(81);

                var output = s.Sort().ToArray();

                output.Should().Equal(source.OrderByDescending(x => x.Key).ToArray());
            }
        }
    }
}
