using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void Incoming_only_links_at_every_level_retain_tombstone_until_reconciliation(int level)
        {
            using var file = new TempFile();
            PageAddress removed = PageAddress.Empty;
            using (var db = new LiteDatabase(file.Filename))
            {
                BuildBoundedRepairGraph(db, 65, arms: false);
                AutoTransactionMethod.MakeGenericMethod(typeof(bool)).Invoke(EngineField.GetValue(db), new object[]
                {
                    new Func<TransactionService, bool>(transaction =>
                    {
                        var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
                        var metadata = snapshot.CollectionPage.GetVectorIndexMetadata("embedding_idx");
                        var root = snapshot.GetPage<VectorIndexPage>(metadata.Root.PageID).GetNode(metadata.Root.Index);
                        removed = root.GetNeighbors(0)[0];
                        root.SetLevelCount(VectorIndexNode.MaxLevels);
                        root.SetNeighbors(level, root.GetNeighbors(level).Concat(new[] { removed }).Distinct().ToArray());
                        var endpoint = snapshot.GetPage<VectorIndexPage>(removed.PageID).GetNode(removed.Index);
                        endpoint.SetLevelCount(VectorIndexNode.MaxLevels);
                        endpoint.RemoveNeighbor(level, metadata.Root);
                        return true;
                    })
                });
                db.Checkpoint();
                var docs = db.GetCollection<VectorDocument>("docs");
                db.BeginTrans().Should().BeTrue();
                docs.Delete(2).Should().BeTrue();
                RepairDeferred(db).Should().BeTrue();
                AssertAllocatedVectorLinks(db, 65);
                db.Rollback().Should().BeTrue();
                RepairDeferred(db).Should().BeFalse();
                docs.FindById(2).Should().NotBeNull();
                docs.Delete(2).Should().BeTrue();
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                RepairDeferred(db).Should().BeTrue();
                AssertAllocatedVectorLinks(db, 65);
                var docs = db.GetCollection<VectorDocument>("docs");
                docs.Insert(new VectorDocument { Id = 999, Embedding = BoundedVector(-999) });
                RepairDeferred(db).Should().BeFalse();
                AssertConnectivity(db, 65);
                AssertAllocatedVectorLinks(db, 65);
                db.Checkpoint();
                // Force reclaimed physical pages (including the former endpoint's
                // slot) into another collection. A stale edge must never cross
                // ownership and reach these unrelated nodes.
                var reuse = db.GetCollection<VectorDocument>("reuse");
                reuse.Insert(Enumerable.Range(1, 128).Select(id => new VectorDocument { Id = id, Embedding = BoundedVector(id) }));
                reuse.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(256));
                var oldPage = db.Execute("SELECT $ FROM $dump WHERE $.pageID = @0", (int)removed.PageID).Single();
                oldPage["collection"].AsString.Should().Be("reuse");
                AssertAllocatedVectorLinks(db, 65);
                docs.Query().TopKNear(x => x.Embedding, BoundedVector(-999), 65).ToArray()
                    .Select(x => x.Id).Should().BeEquivalentTo(docs.FindAll().Select(x => x.Id));
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                RepairDeferred(db).Should().BeFalse();
                AssertConnectivity(db, 65);
                AssertAllocatedVectorLinks(db, 65);
                db.GetCollection<VectorDocument>("reuse").Count().Should().Be(128);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Disconnected_fixture_drop_and_rebuild_release_all_allocated_vector_pages(bool rebuild)
        {
            using var file = new TempFile();
            using (var resource = typeof(VectorIndex_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.HnswDisconnected_dev_b3c440623.zip"))
            using (var archive = new ZipArchive(resource))
            using (var input = archive.GetEntry("vectors.db").Open())
            using (var output = File.Create(file.Filename)) input.CopyTo(output);
            BsonDocument[] expected;
            using (var db = new LiteDatabase(file.Filename))
            {
                var docs = db.GetCollection("computed");
                expected = docs.FindAll().ToArray();
                AllocatedVectorPages(db, "computed").Should().BeGreaterThan(0);
                if (rebuild) db.Rebuild();
                db.BeginTrans().Should().BeTrue();
                docs.DropIndex("coalesced").Should().BeTrue();
                AllocatedVectorPages(db, "computed").Should().Be(0);
                db.Rollback().Should().BeTrue();
                AllocatedVectorPages(db, "computed").Should().BeGreaterThan(0);
                docs.DropIndex("coalesced").Should().BeTrue();
                AllocatedVectorPages(db, "computed").Should().Be(0);
                docs.FindAll().Should().BeEquivalentTo(expected);
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                AllocatedVectorPages(db, "computed").Should().Be(0);
                db.GetCollection("computed").FindAll().Should().BeEquivalentTo(expected);
            }
        }

        [Theory]
        [InlineData(2)]
        [InlineData(2048)]
        public void Disconnected_retry_and_drop_reclaim_pages_and_preserve_sibling_indexes(int dimensions)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { Filename = file.Filename, TransactionPageLimit = 1 })))
            {
                var docs = db.GetCollection<VectorDocument>("docs");
                docs.Insert(Enumerable.Range(1, 24).Select(id => new VectorDocument
                    { Id = id, Embedding = Enumerable.Repeat((float)id, dimensions).ToArray() }));
                using var control = new LiteDatabase(":memory:");
                control.GetCollection<VectorDocument>("docs").Insert(docs.FindAll().Where(x => x.Id != 2));
                var documentPages = control.Execute("SELECT $ FROM $dump WHERE $.collection = 'docs' AND $.pageType = 'Data'").ToArray().Length;
                docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions((ushort)dimensions));
                docs.EnsureIndex("sibling", "$.Embedding", new VectorIndexOptions((ushort)dimensions, VectorDistanceMetric.Euclidean));
                AutoTransactionMethod.MakeGenericMethod(typeof(bool)).Invoke(EngineField.GetValue(db), new object[]
                {
                    new Func<TransactionService, bool>(transaction =>
                    {
                        var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
                        foreach (var id in snapshot.GetVectorPages())
                        {
                            var page = snapshot.GetPage<VectorIndexPage>(id);
                            foreach (var node in page.GetNodes())
                                for (var l = 0; l < VectorIndexNode.MaxLevels; l++) node.SetNeighbors(l, Array.Empty<PageAddress>());
                            snapshot.Safepoint();
                        }
                        snapshot.CollectionPage.GetVectorIndexMetadata("embedding_idx").Root = PageAddress.Empty;
                        snapshot.CollectionPage.GetCollectionIndex("embedding_idx").VectorRepairDeferred = true;
                        snapshot.CollectionPage.IsDirty = true;
                        return true;
                    })
                });
                db.Checkpoint();
                db.BeginTrans().Should().BeTrue();
                docs.Delete(2).Should().BeTrue();
                RepairDeferred(db).Should().BeFalse();
                AssertAllocatedVectorLinks(db, 46);
                db.Rollback().Should().BeTrue();
                RepairDeferred(db).Should().BeTrue();
                docs.Delete(2).Should().BeTrue();
                AssertAllocatedVectorLinks(db, 46);
                docs.DropIndex("embedding_idx").Should().BeTrue();
                db.Execute("SELECT SUM(*.itemsCount) AS nodes FROM $dump WHERE $.collection = 'docs' AND $.pageType = 'VectorIndex'")
                    .Single()["nodes"].AsInt32.Should().Be(23);
                var expression = BsonExpression.Create("$.Embedding");
                docs.Query().TopKNear(expression, Enumerable.Repeat(3f, dimensions).ToArray(), 23).ToArray()
                    .Select(x => x.Id).Should().BeEquivalentTo(docs.FindAll().Select(x => x.Id));
                docs.DropIndex("sibling").Should().BeTrue();
                AllocatedVectorPages(db, "docs").Should().Be(0);
                // Vector external data must be gone along with vector nodes.
                var withIndexes = db.Execute("SELECT $ FROM $dump WHERE $.collection = 'docs' AND $.pageType = 'Data'").ToArray().Length;
                docs.Count().Should().Be(23);
                withIndexes.Should().Be(documentPages);
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                AllocatedVectorPages(db, "docs").Should().Be(0);
                db.GetCollection<VectorDocument>("docs").Count().Should().Be(23);
            }
        }

        private static int AllocatedVectorPages(LiteDatabase db, string collection)
        {
            // The diagnostic "$" snapshot cannot see another snapshot's dirty,
            // unflushed pages. Inspect pending writes through their actual owner.
            var transaction = ((LiteEngine)EngineField.GetValue(db)).GetMonitor().GetThreadTransaction();
            if (transaction != null) return transaction.CreateSnapshot(LockMode.Read, collection, false).GetVectorPages().Count;
            return db.Execute("SELECT $ FROM $dump WHERE $.collection = @0 AND $.pageType = 'VectorIndex'", collection).ToArray().Length;
        }

        private static void AssertAllocatedVectorLinks(LiteDatabase db, int expectedNodes)
        {
            InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
            {
                var allocated = new HashSet<PageAddress>();
                var links = new List<PageAddress>();
                foreach (var id in snapshot.GetVectorPages())
                {
                    foreach (var node in snapshot.GetPage<VectorIndexPage>(id).GetNodes())
                    {
                        allocated.Add(node.Position);
                        for (var level = 0; level < VectorIndexNode.MaxLevels; level++) links.AddRange(node.GetNeighbors(level));
                    }
                    snapshot.Safepoint();
                }
                allocated.Should().HaveCount(expectedNodes);
                links.Should().OnlyContain(link => allocated.Contains(link), "all persisted links must target allocated nodes owned by this collection");
                return true;
            }).Should().BeTrue();
        }
    }
}
