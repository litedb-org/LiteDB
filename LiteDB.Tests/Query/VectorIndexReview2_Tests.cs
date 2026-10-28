using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public partial class VectorIndex_Tests
    {
        [Theory]
        [InlineData(1009, 1, VectorDistanceMetric.Cosine)]
        [InlineData(1009, 7, VectorDistanceMetric.Euclidean)]
        [InlineData(1009, 2048, VectorDistanceMetric.DotProduct)]
        [InlineData(2053, 1, VectorDistanceMetric.Euclidean)]
        [InlineData(2053, 7, VectorDistanceMetric.DotProduct)]
        [InlineData(2053, 2048, VectorDistanceMetric.Cosine)]
        public void Review2_seeded_file_graph_property(int seed, int dimensions, VectorDistanceMetric metric)
        {
            var filename = Path.Combine(Path.GetTempPath(), "review2-hnsw-" + Guid.NewGuid().ToString("N") + ".db");
            var model = new Dictionary<int, VectorDocument>();
            var random = new Random(seed);
            LiteDatabase db = null;
            VectorIndexService.LevelRandomFactory = () => new Random(seed);
            float[] Vector(int id) => Enumerable.Range(0, dimensions)
                .Select(d => id % 5 == 0 ? 1f : id % 7 == 0 ? 0f : (float)(random.Next(-4, 5))).ToArray();
            try
            {
                db = new LiteDatabase(new LiteEngine(new EngineSettings { Filename = filename, TransactionPageLimit = 1 }));
                db.CheckpointSize = 0;
                db.GetCollection<VectorDocument>("docs").EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions((ushort)dimensions, metric));
                Review2Verify(db, model, Vector(3), metric);
                for (var id = 1; id <= 36; id++)
                {
                    var document = new VectorDocument { Id = id, Embedding = Vector(id) };
                    db.GetCollection<VectorDocument>("docs").Insert(document);
                    model[id] = document;
                    Review2Verify(db, model, Vector(3), metric);
                }
                for (var step = 0; step < 36; step++)
                {
                    var before = new Dictionary<int, VectorDocument>(model);
                    db.BeginTrans().Should().BeTrue();
                    var id = random.Next(1, 45);
                    if (step % 4 == 0)
                    {
                        id = Review2RootId(db);
                        db.GetCollection<VectorDocument>("docs").Delete(id).Should().BeTrue();
                        model.Remove(id);
                    }
                    else
                    {
                        var document = new VectorDocument { Id = id, Embedding = Vector(id) };
                        db.GetCollection<VectorDocument>("docs").Upsert(document);
                        model[id] = document;
                    }
                    Review2Verify(db, model, Vector(3), metric);
                    if (step % 5 == 0) { db.Rollback().Should().BeTrue(); model = before; }
                    else db.Commit().Should().BeTrue();
                    Review2Verify(db, model, Vector(3), metric);
                    if (step % 12 == 11)
                    {
                        db.Checkpoint();
                        db.Dispose();
                        db = new LiteDatabase(new LiteEngine(new EngineSettings { Filename = filename, TransactionPageLimit = 1 }));
                        db.CheckpointSize = 0;
                        Review2Verify(db, model, Vector(3), metric);
                        db.Rebuild();
                        Review2Verify(db, model, Vector(3), metric);
                    }
                }
                while (model.Count > 0)
                {
                    var id = Review2RootId(db);
                    db.GetCollection<VectorDocument>("docs").Delete(id).Should().BeTrue();
                    model.Remove(id);
                    Review2Verify(db, model, Vector(3), metric);
                }
                db.Checkpoint();
                db.Dispose();
                db = new LiteDatabase(filename);
                Review2Verify(db, model, Vector(3), metric);
            }
            finally
            {
                db?.Dispose();
                VectorIndexService.LevelRandomFactory = null;
                File.Delete(filename);
                File.Delete(FileHelper.GetLogFile(filename));
            }
        }

        private static int Review2RootId(LiteDatabase db) => InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
        {
            var root = snapshot.GetPage<VectorIndexPage>(metadata.Root.PageID).GetNode(metadata.Root.Index);
            return new DatafileLookup(new DataService(snapshot, snapshot.MaxItemsCount), false, null).Load(root.DataBlock)["_id"].AsInt32;
        });

        private static void Review2Verify(LiteDatabase db, Dictionary<int, VectorDocument> model, float[] target, VectorDistanceMetric metric)
        {
            var stored = db.GetCollection<VectorDocument>("docs").FindAll().OrderBy(x => x.Id).ToArray();
            stored.Select(x => x.Id).Should().Equal(model.Keys.OrderBy(x => x));
            foreach (var document in stored) document.Embedding.Should().Equal(model[document.Id].Embedding);
            InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
            {
                metadata.Root.IsEmpty.Should().Be(model.Count == 0);
                var visited = new HashSet<PageAddress>();
                var blocks = new HashSet<PageAddress>();
                var ids = new HashSet<int>();
                var pending = new Stack<PageAddress>();
                if (!metadata.Root.IsEmpty) pending.Push(metadata.Root);
                byte maximumLevel = 0;
                while (pending.Count > 0)
                {
                    var address = pending.Pop();
                    if (!visited.Add(address)) continue;
                    var node = snapshot.GetPage<VectorIndexPage>(address.PageID).GetNode(address.Index);
                    blocks.Add(node.DataBlock).Should().BeTrue("one vector node per document");
                    var document = new DatafileLookup(new DataService(snapshot, snapshot.MaxItemsCount), false, null).Load(node.DataBlock);
                    ids.Add(document["_id"].AsInt32).Should().BeTrue();
                    maximumLevel = Math.Max(maximumLevel, node.LevelCount);
                    for (var level = 0; level < VectorIndexNode.MaxLevels; level++)
                    {
                        var links = node.GetNeighbors(level);
                        links.Count.Should().BeLessThanOrEqualTo(VectorIndexNode.MaxNeighborsPerLevel);
                        links.Distinct().Count().Should().Be(links.Count);
                        links.Should().NotContain(address).And.NotContain(PageAddress.Empty);
                        if (level >= node.LevelCount) links.Should().BeEmpty();
                        foreach (var neighbor in links)
                        {
                            var other = snapshot.GetPage<VectorIndexPage>(neighbor.PageID).GetNode(neighbor.Index);
                            other.LevelCount.Should().BeGreaterThan((byte)level);
                            other.GetNeighbors(level).Should().Contain(address);
                            if (level == 0) pending.Push(neighbor);
                        }
                    }
                }
                ids.Should().BeEquivalentTo(model.Keys);
                if (model.Count > 0) snapshot.GetPage<VectorIndexPage>(metadata.Root.PageID).GetNode(metadata.Root.Index).LevelCount.Should().Be(maximumLevel);
                return true;
            }).Should().BeTrue();
            var actual = db.GetCollection<VectorDocument>("docs").Query().TopKNear(x => x.Embedding, target, 12).ToArray().Select(x => x.Id);
            var actualIds = actual.ToArray();
            actualIds.Distinct().Count().Should().Be(actualIds.Length);
            var expected = ComputeExpectedRanking(model.Values, target, metric, 12).ToArray();
            actualIds.Length.Should().Be(expected.Length);
            for (var i = 0; i < actualIds.Length; i++)
                ComputeReferenceMetrics(model[actualIds[i]].Embedding, target, metric).Distance
                    .Should().BeApproximately(expected[i].Distance, 1e-10, "score-equivalent ties may have different ids/order");
        }
    }
}
