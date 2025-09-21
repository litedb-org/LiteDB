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

        [Fact]
        public void Sort_With_Multiple_Orders_Should_Honor_All_Segments()
        {
            var orders = new[] { Query.Ascending, Query.Descending };

            var tuples = Enumerable.Range(0, 5000)
                .Select(i => new
                {
                    Primary = i / 4,
                    Secondary = ((char)('a' + (i % 4))).ToString(),
                    Address = new PageAddress((uint)i, (byte)(i % 256))
                })
                .ToArray();

            var source = tuples
                .Select(item =>
                {
                    var key = SortKey.FromValues(new BsonValue[]
                    {
                        new BsonValue(item.Primary),
                        new BsonValue(item.Secondary)
                    }, orders);

                    return new KeyValuePair<BsonValue, PageAddress>(key, item.Address);
                })
                .ToArray();

            var pragmas = new EnginePragmas(null);
            pragmas.Set(Pragmas.COLLATION, Collation.Binary.ToString(), false);

            using (var tempDisk = new SortDisk(_factory, 8192, pragmas))
            using (var sorter = new SortService(tempDisk, orders, pragmas))
            {
                sorter.Insert(source);

                sorter.Count.Should().Be(source.Length);
                sorter.Containers.Count.Should().BeGreaterThan(1);

                var output = sorter.Sort().ToArray();

                var expected = tuples
                    .OrderBy(x => x.Primary)
                    .ThenByDescending(x => x.Secondary, StringComparer.Ordinal)
                    .Select(x => x.Address)
                    .ToArray();

                output.Select(x => x.Value).Should().Equal(expected);
            }
        }
    }
}
