using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public partial class VectorIndex_Tests
    {
        [Fact]
        [Trait("Issue", "2906")]
        public void VectorIndex_Search_Prunes_Node_Visits()
        {
            var stats = BuildSeededPruningGraph(42);
            stats.Total.Should().BeGreaterThan(stats.Visited);
            stats.Total.Should().BeGreaterOrEqualTo(64);
            stats.Matches.Should().NotBeEmpty().And.OnlyContain(id => id >= 1 && id <= 64);
        }

        [Fact]
        [Trait("Issue", "2906")]
        public void VectorIndex_Seed_Reproduces_Topology_And_Search()
        {
            var first = BuildSeededPruningGraph(42);
            for (var i = 0; i < 3; i++)
            {
                var repeated = BuildSeededPruningGraph(42);
                repeated.Topology.Should().Be(first.Topology);
                repeated.Visited.Should().Be(first.Visited);
                repeated.Matches.Should().Equal(first.Matches);
            }
            BuildSeededPruningGraph(12345).Topology.Should().NotBe(first.Topology);
        }

        private static (int Visited, int Total, int[] Matches, string Topology) BuildSeededPruningGraph(int seed)
        {
            // Tests run without collection parallelism. Restore the factory even
            // when graph construction or an assertion fails; production has no hook.
            var previous = VectorIndexService.LevelRandomFactory;
            VectorIndexService.LevelRandomFactory = () => new Random(seed);
            try
            {
                using var db = new LiteDatabase(":memory:");
                var collection = db.GetCollection<VectorDocument>("vectors");
                var documents = Enumerable.Range(0, 128).Select(i => new VectorDocument
                {
                    Id = i + 1,
                    Embedding = i < 64 ? new[] { 1f, i / 100f } : new[] { -1f, 2f + (i - 64) / 100f },
                    Flag = i < 64
                }).ToArray();
                collection.Insert(documents);
                collection.Count().Should().Be(128);
                collection.EnsureIndex("embedding_idx", "$.Embedding",
                    new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));

                return InspectVectorIndex(db, "vectors", (snapshot, collation, metadata) =>
                {
                    var service = new VectorIndexService(snapshot, collation);
                    var matches = service.Search(metadata, new[] { 1f, 0f }, maxDistance: 0.25, limit: 5).ToList();
                    var visited = new HashSet<PageAddress>();
                    var queue = new Queue<PageAddress>();
                    var topology = new List<string>();
                    queue.Enqueue(metadata.Root);
                    while (queue.Count > 0)
                    {
                        var address = queue.Dequeue();
                        if (address.IsEmpty || !visited.Add(address)) continue;
                        var node = snapshot.GetPage<VectorIndexPage>(address.PageID).GetNode(address.Index);
                        topology.Add($"{address}:{node.LevelCount}");
                        for (var level = 0; level < node.LevelCount; level++)
                        {
                            var neighbors = node.GetNeighbors(level);
                            topology.Add(string.Join(",", neighbors));
                            foreach (var neighbor in neighbors) queue.Enqueue(neighbor);
                        }
                    }
                    return (service.LastVisitedCount, visited.Count,
                        matches.Select(x => x.Document["_id"].AsInt32).ToArray(), string.Join(";", topology));
                });
            }
            finally
            {
                VectorIndexService.LevelRandomFactory = previous;
            }
        }
    }
}
