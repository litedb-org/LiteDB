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
        public void Replayed_levels_keep_every_node_reachable_and_restore_exhaustive_recall()
        {
            VectorIndexService.LevelRandomFactory = () => new ConnectivityLevelRandom();
            try
            {
                using var db = new LiteDatabase(":memory:");
                var collection = db.GetCollection("docs");
                collection.Insert(Enumerable.Range(1, 20).Select(i => new BsonDocument
                {
                    ["_id"] = i,
                    ["Embedding"] = i % 5 == 0 ? BsonValue.Null : new BsonArray(1.0, (double)i)
                }));
                collection.EnsureIndex("embedding_idx", "COALESCE($.Embedding, [0, 0])", new VectorIndexOptions(2));
                AssertConnectivity(db, 20);
                var target = new[] { 1f, 7f };
                var ann = collection.Query().TopKNear(BsonExpression.Create("COALESCE($.Embedding, [0, 0])"), target, 20);
                ann.GetPlan()["index"]["name"].AsString.Should().Be("embedding_idx");
                var exact = collection.Query().WhereNear(BsonExpression.Create("COALESCE($.Embedding, [0, 0])"), target, double.MaxValue);
                ann.ToArray().Select(x => x["_id"].AsInt32).Should()
                    .Equal(exact.ToArray().Select(x => x["_id"].AsInt32));
            }
            finally
            {
                VectorIndexService.LevelRandomFactory = null;
            }
        }

        [Theory]
        [InlineData(17, VectorDistanceMetric.Cosine)]
        [InlineData(29, VectorDistanceMetric.Euclidean)]
        [InlineData(43, VectorDistanceMetric.DotProduct)]
        public void Seeded_mutations_preserve_connectivity_and_exhaustive_recall(int seed, VectorDistanceMetric metric)
        {
            VectorIndexService.LevelRandomFactory = () => new Random(seed);
            try
            {
                var random = new Random(seed);
                for (var sample = 0; sample < 12; sample++)
                {
                    using var db = new LiteDatabase(":memory:");
                    var collection = db.GetCollection<VectorDocument>("docs");
                    collection.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(4, metric));
                    var model = new Dictionary<int, VectorDocument>();
                    for (var step = 1; step <= 60; step++)
                    {
                        var id = random.Next(1, 25);
                        if (step % 3 == 0)
                        {
                            collection.Delete(id);
                            model.Remove(id);
                        }
                        else
                        {
                            var document = new VectorDocument { Id = id, Embedding = CreateVector(random, 4) };
                            collection.Upsert(document);
                            model[id] = document;
                        }
                        AssertConnectivity(db, model.Count);
                        var target = CreateVector(random, 4);
                        var expected = ComputeExpectedRanking(model.Values, target, metric, 8).Select(x => x.Id);
                        collection.Query().TopKNear(x => x.Embedding, target, 8).ToArray()
                            .Select(x => x.Id).Should().Equal(expected, $"seed {seed}, sample {sample}, step {step}");
                    }
                }
            }
            finally
            {
                VectorIndexService.LevelRandomFactory = null;
            }
        }

        [Fact]
        public void Equal_vectors_remain_reachable_after_saturated_neighborhoods_and_root_deletion()
        {
            VectorIndexService.LevelRandomFactory = () => new Random(43);
            try
            {
                using var db = new LiteDatabase(":memory:");
                var collection = db.GetCollection<VectorDocument>("docs");
                collection.Insert(Enumerable.Range(1, 40).Select(i =>
                    new VectorDocument { Id = i, Embedding = new[] { 1f, 1f } }));
                collection.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));
                AssertConnectivity(db, 40);
                for (var remaining = 40; remaining > 0; remaining--)
                {
                    collection.Query().TopKNear(x => x.Embedding, new[] { 1f, 1f }, 40)
                        .ToArray().Select(x => x.Id).Should().BeEquivalentTo(collection.FindAll().Select(x => x.Id));
                    var id = InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
                    {
                        var root = snapshot.GetPage<VectorIndexPage>(metadata.Root.PageID).GetNode(metadata.Root.Index);
                        return new DatafileLookup(new DataService(snapshot, snapshot.MaxItemsCount), false, null)
                            .Load(root.DataBlock)["_id"];
                    });
                    // Delete the actual entry document, including successive highest-level nodes.
                    collection.Delete(id);
                    AssertConnectivity(db, remaining - 1);
                }
            }
            finally
            {
                VectorIndexService.LevelRandomFactory = null;
            }
        }

        [Fact]
        public void Deleting_an_articulation_point_reconnects_every_surviving_component()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("docs");
            collection.Insert(Enumerable.Range(1, 25).Select(i =>
                new VectorDocument { Id = i, Embedding = new[] { 1f, (float)i } }));
            collection.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));
            db.BeginTrans().Should().BeTrue();
            // Acquire the collection's write snapshot before installing a valid
            // reciprocal star with eight arms. This explicitly exercises a cut vertex.
            collection.Update(new VectorDocument { Id = 1, Embedding = new[] { 1f, 1f } }).Should().BeTrue();
            var center = InstallConnectivityStar(db, 25);
            db.Commit().Should().BeTrue();
            AssertConnectivity(db, 25);
            // The nearest node has eight bridge edges and no triangle. Insertion
            // must subdivide one rather than discard it or isolate the new node.
            collection.Insert(new VectorDocument { Id = 26, Embedding = new[] { 1f, 1f } });
            AssertConnectivity(db, 26);
            collection.Delete(center).Should().BeTrue();
            AssertConnectivity(db, 25);
            var target = new[] { 1f, 7.25f };
            collection.Query().TopKNear(x => x.Embedding, target, 26).ToArray().Select(x => x.Id).Should()
                .Equal(ComputeExpectedRanking(collection.FindAll(), target, VectorDistanceMetric.Cosine).Select(x => x.Id));
        }

        private static BsonValue InstallConnectivityStar(LiteDatabase db, int count)
        {
            return InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
            {
                var order = new List<PageAddress>();
                var pending = new Stack<PageAddress>();
                pending.Push(metadata.Root);
                while (pending.Count > 0)
                {
                    var address = pending.Pop();
                    if (order.Contains(address)) continue;
                    order.Add(address);
                    var node = snapshot.GetPage<VectorIndexPage>(address.PageID).GetNode(address.Index);
                    foreach (var neighbor in node.GetNeighbors(0)) pending.Push(neighbor);
                }
                order.Count.Should().Be(count);
                var lookup = new DatafileLookup(new DataService(snapshot, snapshot.MaxItemsCount), false, null);
                var first = order.Single(address => lookup.Load(snapshot.GetPage<VectorIndexPage>(address.PageID)
                    .GetNode(address.Index).DataBlock)["_id"].AsInt32 == 1);
                order.Remove(first);
                order.Insert(0, first);
                metadata.Root = first;
                snapshot.CollectionPage.IsDirty = true;
                for (var i = 0; i < order.Count; i++)
                {
                    var links = new List<PageAddress>();
                    if (i == 0)
                        for (var arm = 0; arm < 8; arm++) links.Add(order[1 + arm * 3]);
                    else
                    {
                        var offset = (i - 1) % 3;
                        links.Add(offset == 0 ? order[0] : order[i - 1]);
                        if (offset < 2 && i + 1 < order.Count) links.Add(order[i + 1]);
                    }
                    var node = snapshot.GetPage<VectorIndexPage>(order[i].PageID).GetNode(order[i].Index);
                    if (i == 0) node.SetLevelCount(VectorIndexNode.MaxLevels);
                    node.SetNeighbors(0, links);
                    for (var level = 1; level < node.LevelCount; level++)
                        node.SetNeighbors(level, Array.Empty<PageAddress>());
                }
                var root = snapshot.GetPage<VectorIndexPage>(order[0].PageID).GetNode(order[0].Index);
                return new DatafileLookup(new DataService(snapshot, snapshot.MaxItemsCount), false, null)
                    .Load(root.DataBlock)["_id"];
            });
        }

        private static void AssertConnectivity(LiteDatabase db, int count)
        {
            InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
            {
                var visited = new HashSet<PageAddress>();
                var pending = new Stack<PageAddress>();
                if (!metadata.Root.IsEmpty) pending.Push(metadata.Root);
                while (pending.Count > 0)
                {
                    var address = pending.Pop();
                    if (!visited.Add(address)) continue;
                    var node = snapshot.GetPage<VectorIndexPage>(address.PageID).GetNode(address.Index);
                    foreach (var neighbor in node.GetNeighbors(0))
                    {
                        neighbor.Should().NotBe(PageAddress.Empty);
                        var other = snapshot.GetPage<VectorIndexPage>(neighbor.PageID).GetNode(neighbor.Index);
                        other.GetNeighbors(0).Should().Contain(address);
                        pending.Push(neighbor);
                    }
                }
                visited.Count.Should().Be(count, "all live vectors must be reachable at level zero");
                return true;
            }).Should().BeTrue();
        }

        private sealed class ConnectivityLevelRandom : Random
        {
            private readonly Queue<double> _draws = new Queue<double>();

            public ConnectivityLevelRandom()
            {
                foreach (var level in new[] { 1,1,1,1,1,1,1,1,1,3,1,1,1,4,4,1,2,2,1,1 })
                {
                    for (var i = 1; i < level; i++) _draws.Enqueue(0.25);
                    if (level < 4) _draws.Enqueue(0.75);
                }
            }

            public override double NextDouble() => _draws.Count > 0 ? _draws.Dequeue() : 0.75;
        }
    }
}
