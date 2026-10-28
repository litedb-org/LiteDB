using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public partial class VectorIndex_Tests
    {
        [Theory]
        [InlineData(257, false)]
        [InlineData(4097, false)]
        [InlineData(257, true)]
        [InlineData(4097, true)]
        public void Large_graph_mutations_dirty_only_neighbourhood_pages(int count, bool update)
        {
            using var db = new LiteDatabase(":memory:");
            BuildBoundedRepairGraph(db, count, arms: false);
            db.BeginTrans().Should().BeTrue();
            var docs = db.GetCollection<VectorDocument>("docs");
            if (update) docs.Update(new VectorDocument { Id = 2, Embedding = BoundedVector(2) }).Should().BeTrue();
            else docs.Delete(2).Should().BeTrue();
            InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
            {
                snapshot.LocalPages.OfType<VectorIndexPage>().Count(x => x.IsDirty)
                    .Should().BeLessThanOrEqualTo(32, "only backlinks, bridges and insertion neighbours may be written");
                snapshot.CollectionPage.GetVectorIndexes().Single().Index.VectorRepairDeferred.Should().BeFalse();
                return true;
            }).Should().BeTrue();
            db.Commit().Should().BeTrue();
            AssertConnectivity(db, update ? count : count - 1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Budget_exhaustion_succeeds_bounds_writes_and_survives_rollback_reopen_and_rebuild(bool update)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                BuildBoundedRepairGraph(db, 2049, arms: true);
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
                db.Checkpoint();
                db.CheckpointSize = 0;
                db.BeginTrans().Should().BeTrue();
                MutateDeferredGraph(db, update);
                AssertDeferredRepair(db, update);
                InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
                {
                    snapshot.LocalPages.OfType<VectorIndexPage>().Count(x => x.IsDirty).Should().Be(0,
                        "exhausted repair retains the original tree and writes only its metadata signal");
                    return true;
                }).Should().BeTrue();
                db.Rollback().Should().BeTrue();
                db.GetCollection<VectorDocument>("docs").Count().Should().Be(2049);
                db.GetCollection<VectorDocument>("docs").Query().TopKNear(x => x.Embedding, BoundedVector(1), 1)
                    .ToArray().Should().ContainSingle();
                db.BeginTrans().Should().BeTrue();
                MutateDeferredGraph(db, update);
                db.Commit().Should().BeTrue();
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var db = new LiteDatabase(file.Filename);
                AssertDeferredRepair(db, update);
                db.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("untouched");
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                // While deferred, ordinary CRUD still succeeds; the retained graph
                // must never dereference old document addresses during maintenance.
                var docs = db.GetCollection<VectorDocument>("docs");
                docs.Delete(2).Should().BeTrue();
                docs.Insert(new VectorDocument { Id = 3000, Embedding = BoundedVector(3000) });
                docs.Update(new VectorDocument { Id = 3, Embedding = BoundedVector(-3) }).Should().BeTrue();
                var expected = docs.FindAll().OrderBy(x => x.Id).ToArray();
                db.Rebuild();
                AssertConnectivity(db, expected.Length);
                docs.FindAll().OrderBy(x => x.Id).Select(x => x.Id).Should().Equal(expected.Select(x => x.Id));
                foreach (var document in docs.FindAll()) document.Embedding.Should().Equal(expected.Single(x => x.Id == document.Id).Embedding);
                var target = BoundedVector(-1);
                docs.Query().TopKNear(x => x.Embedding, target, expected.Length).ToArray().Select(x => x.Id).Should()
                    .Equal(ComputeExpectedRanking(expected, target, VectorDistanceMetric.Euclidean).Select(x => x.Id));
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection<VectorDocument>("docs").Query().TopKNear(x => x.Embedding, BoundedVector(1), 1)
                    .ToArray().Should().ContainSingle();
                db.GetCollection<VectorDocument>("docs").DropIndex("embedding_idx").Should().BeTrue();
            }
        }

        [Fact]
        public void Saturated_split_components_keep_existing_edges_and_use_spare_endpoints()
        {
            using var db = new LiteDatabase(":memory:");
            BuildBoundedRepairGraph(db, 65, arms: true);
            db.BeginTrans().Should().BeTrue();
            // Upgrade the snapshot to write mode before installing eight cliques.
            db.GetCollection<VectorDocument>("docs").Update(new VectorDocument { Id = 1, Embedding = BoundedVector(1) });
            var edges = InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
            {
                CountNodes(snapshot, metadata.Root).Should().Be(65);
                var lookup = new DatafileLookup(new DataService(snapshot, snapshot.MaxItemsCount), false, null);
                var nodes = snapshot.LocalPages.OfType<VectorIndexPage>().SelectMany(x => x.GetNodes())
                    .ToDictionary(node => lookup.Load(node.DataBlock)["_id"].AsInt32, node => node.Position);
                var original = new Dictionary<PageAddress, PageAddress[]>();
                for (var id = 1; id <= 65; id++)
                {
                    var links = id == 1 ? Enumerable.Range(0, 8).Select(i => nodes[2 + i * 8]).ToArray()
                        : Enumerable.Range(2 + (id - 2) / 8 * 8, 8).Where(other => other != id).Select(other => nodes[other])
                            .Concat((id - 2) % 8 == 0 ? new[] { nodes[1] } : Array.Empty<PageAddress>()).ToArray();
                    snapshot.GetPage<VectorIndexPage>(nodes[id].PageID).GetNode(nodes[id].Index).SetNeighbors(0, links);
                    if (id > 1) original.Add(nodes[id], links.Where(x => x != nodes[1]).ToArray());
                }
                metadata.Root = nodes[1];
                snapshot.CollectionPage.IsDirty = true;
                return original;
            });
            db.Commit().Should().BeTrue();
            db.GetCollection<VectorDocument>("docs").Delete(1).Should().BeTrue();
            AssertConnectivity(db, 64);
            InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
            {
                foreach (var pair in edges)
                {
                    var links = snapshot.GetPage<VectorIndexPage>(pair.Key.PageID).GetNode(pair.Key.Index).GetNeighbors(0);
                    links.Should().Contain(pair.Value).And.HaveCountLessThanOrEqualTo(8);
                }
                return true;
            }).Should().BeTrue();
        }

        private static void MutateDeferredGraph(LiteDatabase db, bool update)
        {
            var docs = db.GetCollection<VectorDocument>("docs");
            if (update) docs.Update(new VectorDocument { Id = 1, Embedding = BoundedVector(-1) }).Should().BeTrue();
            else docs.Delete(1).Should().BeTrue();
        }

        private static void AssertDeferredRepair(LiteDatabase db, bool update)
        {
            var docs = db.GetCollection<VectorDocument>("docs");
            docs.Count().Should().Be(update ? 2049 : 2048);
            Action ann = () => docs.Query().TopKNear(x => x.Embedding, BoundedVector(1), 10).ToArray();
            ann.Should().Throw<LiteException>().WithMessage("*repair deferred*1024*Rebuild()*");
            docs.Query().WhereNear(x => x.Embedding, BoundedVector(1), double.MaxValue).ToArray()
                .Select(x => x.Id).Should().BeEquivalentTo(docs.FindAll().Select(x => x.Id));
            if (update) docs.FindById(1).Embedding.Should().Equal(BoundedVector(-1));
            else docs.FindById(1).Should().BeNull();
        }

        private static float[] BoundedVector(int id) => Enumerable.Range(0, 256).Select(i => i == 0 ? (float)id : 1f).ToArray();

        // Install a synthetic reciprocal graph directly, keeping setup independent
        // of ANN heuristics and avoiding a timed workload. 256 inline dimensions
        // force hundreds of distinct pages, so rewriting all survivors cannot hide.
        private static void BuildBoundedRepairGraph(LiteDatabase db, int count, bool arms)
        {
            var docs = db.GetCollection<VectorDocument>("docs");
            docs.Insert(Enumerable.Range(1, count).Select(id => new VectorDocument { Id = id, Embedding = BoundedVector(id) }));
            var engine = (LiteEngine)EngineField.GetValue(db);
            var collation = ((HeaderPage)HeaderField.GetValue(engine)).Pragmas.Collation;
            AutoTransactionMethod.MakeGenericMethod(typeof(bool)).Invoke(engine, new object[]
            {
                new Func<TransactionService, bool>(transaction =>
                {
                    var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
                    snapshot.RequireVectorVersion();
                    var metadata = snapshot.CollectionPage.InsertVectorIndex("embedding_idx", "$.Embedding", 256,
                        VectorDistanceMetric.Euclidean).Metadata;
                    var indexer = new IndexService(snapshot, collation, snapshot.MaxItemsCount);
                    var blocks = indexer.FindAll(snapshot.CollectionPage.PK, Query.Ascending).Select(x => x.DataBlock).ToArray();
                    var addresses = new List<PageAddress>();
                    var length = VectorIndexNode.GetLength(256, out var inline);
                    inline.Should().BeTrue();
                    for (var i = 0; i < count; i++)
                    {
                        var free = metadata.Reserved;
                        var page = snapshot.GetFreeVectorPage(length, ref free);
                        addresses.Add(page.InsertNode(blocks[i], BoundedVector(i + 1), length, 1, PageAddress.Empty).Position);
                        metadata.Reserved = uint.MaxValue;
                        snapshot.AddOrRemoveFreeVectorList(page, ref free);
                        metadata.Reserved = free;
                    }
                    metadata.Root = addresses[0];
                    var adjacency = addresses.ToDictionary(x => x, x => new List<PageAddress>());
                    void Link(int left, int right)
                    {
                        adjacency[addresses[left]].Add(addresses[right]);
                        adjacency[addresses[right]].Add(addresses[left]);
                    }
                    if (arms)
                    {
                        for (var arm = 1; arm <= 8; arm++)
                        {
                            Link(0, arm);
                            for (var i = arm; i + 8 < count; i += 8) Link(i, i + 8);
                        }
                    }
                    else
                    {
                        for (var i = 1; i < count; i++) Link(i - 1, i);
                        Link(0, 2); // Former neighbours of id 2 meet immediately.
                    }
                    foreach (var pair in adjacency)
                        snapshot.GetPage<VectorIndexPage>(pair.Key.PageID).GetNode(pair.Key.Index).SetNeighbors(0, pair.Value);
                    snapshot.CollectionPage.IsDirty = true;
                    return true;
                })
            });
            db.Checkpoint();
        }
    }
}
