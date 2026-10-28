using System;
using System.Collections.Generic;
using System.IO;
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
        [Fact]
        public void Review2_hnswd_b_asymmetric_reclaims_empty_graph() => ReviewBWorkload(true);

        [Fact]
        public void Review2_hnswd_b_fresh_reclaims_empty_graph() => ReviewBWorkload(false);

        private static void ReviewBWorkload(bool asymmetric)
        {
            using var levels = new MutationLevelScope();
            using var file = new TempFile();
            using var dropFile = new TempFile();
            var live = new Dictionary<int, float[]>();
            var retained = 0;
            using (var db = new LiteDatabase(file.Filename))
            {
                var docs = db.GetCollection<VectorDocument>("docs");
                docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(256, VectorDistanceMetric.Euclidean));
                ReviewBMeasure(db, live, asymmetric, "baseline").Should().Be(0);
                if (asymmetric)
                {
                    docs.DropIndex("embedding_idx").Should().BeTrue();
                    BuildBoundedRepairGraph(db, 5000, arms: false);
                    AutoTransactionMethod.MakeGenericMethod(typeof(bool)).Invoke(EngineField.GetValue(db), new object[]
                    {
                        new Func<TransactionService, bool>(transaction =>
                        {
                            var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
                            var metadata = snapshot.CollectionPage.GetVectorIndexMetadata("embedding_idx");
                            var root = snapshot.GetPage<VectorIndexPage>(metadata.Root.PageID).GetNode(metadata.Root.Index);
                            var endpoint = root.GetNeighbors(0)[0];
                            snapshot.GetPage<VectorIndexPage>(endpoint.PageID).GetNode(endpoint.Index).RemoveNeighbor(0, metadata.Root);
                            return true;
                        })
                    });
                }
                else docs.Insert(Enumerable.Range(1, 5000).Select(id => new VectorDocument { Id = id, Embedding = BoundedVector(id) }));
                foreach (var id in Enumerable.Range(1, 5000)) live.Add(id, BoundedVector(id));
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
                db.Checkpoint();
                ReviewBMeasure(db, live, asymmetric, "built");
                ReviewBCheckQueries(db, live);
                ReviewBDelete(db, live, Enumerable.Range(2, 1000));
                ReviewBMeasure(db, live, asymmetric, "delete1000");
                ReviewBCheckQueries(db, live);
                docs.Insert(Enumerable.Range(5001, 1000).Select(id => new VectorDocument { Id = id, Embedding = BoundedVector(id) }));
                foreach (var id in Enumerable.Range(5001, 1000)) live.Add(id, BoundedVector(id));
                ReviewBMeasure(db, live, asymmetric, "insert1000");
                ReviewBCheckQueries(db, live);
                ReviewBDelete(db, live, Enumerable.Range(1002, 1000));
                ReviewBMeasure(db, live, asymmetric, "delete2000");
                ReviewBCheckQueries(db, live);
                docs.DeleteAll().Should().Be(live.Count);
                live.Clear();
                retained = ReviewBMeasure(db, live, asymmetric, "empty");
                ReviewBCheckQueries(db, live);
                db.Checkpoint();
            }
            File.Copy(file.Filename, dropFile.Filename, true);
            using (var db = new LiteDatabase(dropFile.Filename))
            {
                db.GetCollection<VectorDocument>("docs").DropIndex("embedding_idx").Should().BeTrue();
                AllocatedVectorPages(db, "docs").Should().Be(0);
                ReviewBLog($"graph={(asymmetric ? "asymmetric" : "fresh")} phase=drop live=0 tombstones=0 pages=0 nodes=0 reachable=0");
                db.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("untouched");
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(dropFile.Filename)) AllocatedVectorPages(db, "docs").Should().Be(0);
            using (var db = new LiteDatabase(file.Filename))
            {
                ReviewBMeasure(db, live, asymmetric, "reopen_empty").Should().Be(retained);
                ReviewBCheckQueries(db, live);
                var docs = db.GetCollection<VectorDocument>("docs");
                docs.Insert(new VectorDocument { Id = 2, Embedding = BoundedVector(-2) });
                live.Add(2, BoundedVector(-2));
                ReviewBMeasure(db, live, asymmetric, "same_id_new_vector");
                ReviewBCheckQueries(db, live);
                docs.Query().WhereNear(x => x.Embedding, BoundedVector(2), 0).ToArray().Should().BeEmpty();
                docs.Query().TopKNear(x => x.Embedding, BoundedVector(-2), 1).ToArray().Single().Embedding.Should().Equal(BoundedVector(-2));
                docs.Delete(2).Should().BeTrue();
                live.Clear();
                docs.Insert(new VectorDocument { Id = 90000, Embedding = BoundedVector(-90000) });
                live.Add(90000, BoundedVector(-90000));
                ReviewBMeasure(db, live, asymmetric, "new_id_new_vector");
                ReviewBCheckQueries(db, live);
                docs.Query().WhereNear(x => x.Embedding, BoundedVector(2), 0).ToArray().Should().BeEmpty();
                docs.DeleteAll().Should().Be(1);
                live.Clear();
                ReviewBMeasure(db, live, asymmetric, "empty_again");
                db.Rebuild();
                ReviewBMeasure(db, live, asymmetric, "rebuild_empty").Should().Be(0);
                RepairDeferred(db).Should().BeFalse();
                docs.Insert(new VectorDocument { Id = 2, Embedding = BoundedVector(-2) });
                live.Add(2, BoundedVector(-2));
                ReviewBMeasure(db, live, asymmetric, "insert_after_rebuild");
                ReviewBCheckQueries(db, live);
                docs.Query().WhereNear(x => x.Embedding, BoundedVector(2), 0).ToArray().Should().BeEmpty();
                db.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("untouched");
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename)) ReviewBCheckQueries(db, live);
            retained.Should().Be(0, "a completely deleted vector collection should release its retained graph without requiring an explicit Rebuild or index drop");
        }

        private static void ReviewBDelete(LiteDatabase db, Dictionary<int, float[]> live, IEnumerable<int> ids)
        {
            // Preserve ordinary per-delete transactions, matching the disconnected reviewer's workload.
            foreach (var id in ids)
            {
                db.GetCollection<VectorDocument>("docs").Delete(id).Should().BeTrue();
                live.Remove(id).Should().BeTrue();
            }
        }

        private static int ReviewBMeasure(LiteDatabase db, Dictionary<int, float[]> live, bool asymmetric, string phase)
        {
            return InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
            {
                var pages = snapshot.GetVectorPages();
                var indexer = new IndexService(snapshot, collation, snapshot.MaxItemsCount);
                var blocks = indexer.FindAll(snapshot.CollectionPage.PK, Query.Ascending).ToDictionary(x => x.DataBlock, x => live[x.Key.AsInt32]);
                var allocated = new HashSet<PageAddress>();
                var links = new List<(PageAddress Source, int Level, PageAddress Target)>();
                var tombstones = 0;
                var staleAliases = 0;
                foreach (var id in pages)
                {
                    foreach (var node in snapshot.GetPage<VectorIndexPage>(id).GetNodes())
                    {
                        allocated.Add(node.Position);
                        var vector = node.ReadVector();
                        if (!blocks.TryGetValue(node.DataBlock, out var current) || !current.SequenceEqual(vector))
                        {
                            tombstones++;
                            if (blocks.ContainsKey(node.DataBlock)) staleAliases++;
                        }
                        for (var level = 0; level < VectorIndexNode.MaxLevels; level++)
                            foreach (var target in node.GetNeighbors(level)) links.Add((node.Position, level, target));
                    }
                    snapshot.Safepoint();
                }
                links.Where(link => !allocated.Contains(link.Target)).Should().BeEmpty();
                var reciprocal = new HashSet<(PageAddress, int, PageAddress)>(links.Select(link => (link.Source, link.Level, link.Target)));
                var asymmetricEdges = links.Count(link => !reciprocal.Contains((link.Target, link.Level, link.Source)));
                var reachable = CountNodes(snapshot, metadata.Root);
                if (!asymmetric && phase == "built") asymmetricEdges.Should().Be(0, "the control must be symmetric at every level");
                var deferred = snapshot.CollectionPage.GetCollectionIndex("embedding_idx").VectorRepairDeferred;
                ReviewBLog($"graph={(asymmetric ? "asymmetric" : "fresh")} phase={phase} live={live.Count} tombstones={tombstones} pages={pages.Count} nodes={allocated.Count} reachable={reachable} asymmetric_edges={asymmetricEdges} stale_block_aliases={staleAliases} deferred={deferred}");
                return pages.Count;
            });
        }

        private static void ReviewBCheckQueries(LiteDatabase db, Dictionary<int, float[]> live)
        {
            var docs = db.GetCollection<VectorDocument>("docs");
            ReviewBLog($"query_check live={live.Count} stage=start");
            docs.Count().Should().Be(live.Count);
            docs.Query().WhereNear(x => x.Embedding, BoundedVector(2), double.MaxValue).Count().Should().Be(live.Count);
            ReviewBLog($"query_check live={live.Count} stage=counts_ok");
            var all = docs.Query().WhereNear(x => x.Embedding, BoundedVector(2), double.MaxValue).ToArray();
            all.Select(x => x.Id).OrderBy(id => id).Should().Equal(live.Keys.OrderBy(id => id));
            foreach (var document in all) document.Embedding.Should().Equal(live[document.Id]);
            ReviewBLog($"query_check live={live.Count} stage=exhaustive_ok");
            var query = docs.Query().TopKNear(x => x.Embedding, BoundedVector(2), 25);
            var top = query.ToArray();
            top.Select(x => x.Id).Should().OnlyHaveUniqueItems();
            foreach (var document in top)
            {
                live.Should().ContainKey(document.Id);
                document.Embedding.Should().Equal(live[document.Id]);
            }
            top.Length.Should().Be(Math.Min(25, live.Count));
            if (RepairDeferred(db))
            {
                query.GetPlan()["index"]["mode"].AsString.Should().Be("VECTOR EXACT SCAN (REPAIR DEFERRED)");
                top.Select(x => x.Id).Should().Equal(live.OrderBy(x => Math.Abs(x.Value[0] - 2)).Take(25).Select(x => x.Key));
                InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
                {
                    Action search = () => new VectorIndexService(snapshot, collation).Search(metadata, BoundedVector(2), double.MaxValue, 25);
                    search.Should().Throw<LiteException>().Where(error => error.ErrorCode == LiteException.VECTOR_REPAIR_DEFERRED);
                    return true;
                }).Should().BeTrue();
            }
            else query.GetPlan()["index"]["mode"].AsString.Should().Be("VECTOR INDEX SEARCH");
            ReviewBLog($"query_check live={live.Count} stage=all_ok");
        }

        private static void ReviewBLog(string value)
        {
            Console.WriteLine(value);
            var journal = Environment.GetEnvironmentVariable("LITEDB_HNSWD_B_JOURNAL");
            if (!string.IsNullOrEmpty(journal)) File.AppendAllText(journal, value + Environment.NewLine);
        }
    }
}
