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

        /// <summary>
        /// This test specifically targets the SortService merge logic for multi-key sorts.
        /// It uses a descending primary key to ensure that the merge logic correctly uses
        /// the full SortKey comparison instead of a faulty hardcoded direction.
        /// This test will fail before the fix in SortService.Sort().
        /// </summary>
        [Fact]
        public void Sort_Multi_Key_Descending_Primary()
        {
            // ARRANGE
            // Source data with duplicate primary keys (age) to test the secondary key (name)
            var longA = new string('A', 900);
            var longB = new string('B', 900);
            var longC = new string('C', 900);
            var longZ = new string('Z', 900);

            var baseData = new (int Age, string Name)[]
            {
                (30, longZ),
                (20, longB),
                (30, longA),
                (10, longC),
                (20, longA)
            };

            var source = Enumerable.Range(0, 12)
                .Select(i => baseData[i % baseData.Length])
                .Select(item => new KeyValuePair<BsonValue, PageAddress>(new BsonArray { item.Age, item.Name }, PageAddress.Empty))
                .ToList();

            // Expected order: Age DESC, then Name ASC
            var expected = source
                .Select(x => x.Key)
                .OrderByDescending(x => x.AsArray[0].AsInt32)
                .ThenBy(x => x.AsArray[1].AsString, StringComparer.Ordinal)
                .Select(x => (Age: x.AsArray[0].AsInt32, Initial: x.AsArray[1].AsString[0]))
                .ToArray();

            var pragmas = new EnginePragmas(null);
            pragmas.Set(Pragmas.COLLATION, Collation.Binary.ToString(), false);

            var orders = new[] { Query.Descending, Query.Ascending };

            // ACT
            using (var tempDisk = new SortDisk(_factory, 8192, pragmas))
            using (var s = new SortService(tempDisk, orders, pragmas))
            {
                s.Insert(source);
                s.Containers.Count.Should().BeGreaterThan(1, "multi-container merge ensures SortService.Sort triggers the merge logic");
                var result = s.Sort()
                    .Select(x => x.Key)
                    .Select(x => (Age: x.AsArray[0].AsInt32, Initial: x.AsArray[1].AsString[0]))
                    .ToArray();

                // ASSERT
                result.Should().Equal(expected);
            }
        }
    }
}
