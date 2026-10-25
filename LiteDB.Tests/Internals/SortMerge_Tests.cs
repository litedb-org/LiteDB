using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class SortMerge_Tests
    {
        [Theory]
        [InlineData(1, false, "en-US/Ordinal")]
        [InlineData(-1, false, "en-US/Ordinal")]
        [InlineData(1, false, "en-US/IgnoreCase")]
        [InlineData(-1, false, "en-US/IgnoreCase")]
        [InlineData(1, true, "en-US/IgnoreCase")]
        [InlineData(-1, true, "en-US/IgnoreCase")]
        public void Merge_matches_previous_tie_order_across_many_containers(int direction, bool compound, string culture)
        {
            var collation = new Collation(culture);
            var pragmas = new EnginePragmas(null);
            pragmas.Set(Pragmas.COLLATION, collation.ToString(), false);
            var orders = compound ? new[] { direction, -direction } : new[] { direction };
            var random = new Random(12345);
            var source = Enumerable.Range(0, 12000).Select(i =>
            {
                var value = random.Next(31);
                BsonValue key = (i % 2 == 0 ? "Group" : "GROUP") + value;
                if (compound) key = SortKey.FromValues(new BsonValue[] { key, random.Next(4) }, orders);
                return new KeyValuePair<BsonValue, PageAddress>(key, new PageAddress((uint)i, 0));
            }).ToArray();
            using var disk = new SortDisk(new StreamFactory(new MemoryStream(), null, true), Constants.PAGE_SIZE, pragmas);
            using var sorter = new SortService(disk, orders, pragmas);
            sorter.Insert(source);
            sorter.Containers.Count.Should().BeGreaterThan(10);
            var effectiveOrder = compound ? Query.Ascending : direction;
            var expected = PreviousMerge(source, sorter.Containers.Select(x => x.Count), collation, effectiveOrder).ToArray();
            sorter.Sort().Select(x => (Key: x.Key.ToString(), Address: x.Value)).Should().Equal(
                expected.Select(x => (Key: x.Key.ToString(), Address: x.Value)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(500)]
        public void Empty_and_single_containers_keep_their_fast_path(int count)
        {
            var pragmas = new EnginePragmas(null);
            using var disk = new SortDisk(new StreamFactory(new MemoryStream(), null, true), Constants.PAGE_SIZE, pragmas);
            using var sorter = new SortService(disk, new[] { Query.Ascending }, pragmas);
            sorter.Insert(Enumerable.Range(0, count).Reverse().Select(i =>
                new KeyValuePair<BsonValue, PageAddress>(i, new PageAddress((uint)i, 0))));
            sorter.Sort().Select(x => x.Key.AsInt32).Should().Equal(Enumerable.Range(0, count));
        }

        [Fact]
        public void Completed_multiway_merge_does_not_replay_exhausted_containers()
        {
            var pragmas = new EnginePragmas(null);
            using var disk = new SortDisk(new StreamFactory(new MemoryStream(), null, true), Constants.PAGE_SIZE, pragmas);
            using var sorter = new SortService(disk, new[] { Query.Ascending }, pragmas);
            sorter.Insert(Enumerable.Range(0, 2000).Select(i =>
                new KeyValuePair<BsonValue, PageAddress>(i, new PageAddress((uint)i, 0))));
            sorter.Sort().Count().Should().Be(2000);
            sorter.Sort().Should().BeEmpty();
        }

        [Fact]
        public void Early_disposal_releases_containers_for_the_next_sort()
        {
            var pragmas = new EnginePragmas(null);
            using var disk = new SortDisk(new StreamFactory(new MemoryStream(), null, true), Constants.PAGE_SIZE, pragmas);
            var source = Enumerable.Range(0, 4000).Reverse().Select(i =>
                new KeyValuePair<BsonValue, PageAddress>(i, new PageAddress((uint)i, 0))).ToArray();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var sorter = new SortService(disk, new[] { Query.Ascending }, pragmas);
                sorter.Insert(source);
                sorter.Sort().Take(5).Select(x => x.Key.AsInt32).Should().Equal(0, 1, 2, 3, 4);
            }
        }

        // Reproduce the previous merge's observable tie policy over the same
        // sorted runs: retain the active run on equal keys, otherwise use the
        // first remaining run among equal minima. Unique reload addresses make
        // accidental tie reordering visible, even when BSON keys compare equal.
        private static IEnumerable<KeyValuePair<BsonValue, PageAddress>> PreviousMerge(
            KeyValuePair<BsonValue, PageAddress>[] source, IEnumerable<int> counts, Collation collation, int order)
        {
            var offset = 0;
            var runs = counts.Select(count =>
            {
                var run = source.Skip(offset).Take(count);
                offset += count;
                return (order == Query.Ascending ? run.OrderBy(x => x.Key, collation) :
                    run.OrderByDescending(x => x.Key, collation)).ToArray();
            }).ToArray();
            var positions = new int[runs.Length];
            var current = 0;
            for (var remaining = source.Length; remaining > 0; remaining--)
            {
                if (positions[current] == runs[current].Length)
                    current = Enumerable.Range(0, runs.Length).First(i => positions[i] < runs[i].Length);
                for (var i = 0; i < runs.Length; i++)
                {
                    if (positions[i] < runs[i].Length &&
                        runs[i][positions[i]].Key.CompareTo(runs[current][positions[current]].Key, collation) * order < 0) current = i;
                }
                yield return runs[current][positions[current]++];
            }
        }
    }
}
