using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        public void Review2_hnswd_census_accepts_foreign_pages_committed_after_writer_snapshot()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                BuildBoundedRepairGraph(db, 65, arms: false);
                db.CheckpointSize = 0;
                db.BeginTrans().Should().BeTrue();
                AutoTransactionMethod.MakeGenericMethod(typeof(bool)).Invoke(EngineField.GetValue(db), new object[]
                {
                    new Func<TransactionService, bool>(transaction =>
                    {
                        transaction.CreateSnapshot(LockMode.Write, "docs", false);
                        return true;
                    })
                });
                Task.Run(() => db.GetCollection("foreign").Insert(new BsonDocument
                    { ["_id"] = 1, ["payload"] = new string('x', 40000) })).GetAwaiter().GetResult();
                Action delete = () => db.GetCollection<VectorDocument>("docs").Delete(2).Should().BeTrue();
                try
                {
                    delete.Should().NotThrow("a commit to another collection must not invalidate vector deletion");
                    db.Commit().Should().BeTrue();
                }
                finally
                {
                    db.Rollback();
                }
                db.GetCollection("foreign").FindById(1)["payload"].AsString.Should().Be(new string('x', 40000));
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection<VectorDocument>("docs").Count().Should().Be(64);
                AssertAllocatedVectorLinks(db, 64);
                db.GetCollection("foreign").FindById(1)["payload"].AsString.Length.Should().Be(40000);
            }
        }

        [Fact]
        public void Review2_hnswd_asymmetric_5000_delete_cycles_reclaim_when_empty()
        {
            using var file = new TempFile();
            var retainedAtEmpty = -1;
            using (var db = new LiteDatabase(file.Filename))
            {
                BuildBoundedRepairGraph(db, 5000, arms: false);
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
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
                db.Checkpoint();
                var docs = db.GetCollection<VectorDocument>("docs");
                var live = new HashSet<int>(Enumerable.Range(1, 5000));
                for (var id = 2; id <= 1001; id++)
                {
                    docs.Delete(id).Should().BeTrue();
                    live.Remove(id);
                }
                RepairDeferred(db).Should().BeTrue();
                AssertAllocatedVectorLinks(db, 5000);
                Console.WriteLine("hnswd_counts deletes=1000 live=4000 allocated=5000 tombstones=1000");
                docs.Insert(Enumerable.Range(5001, 1000).Select(id => new VectorDocument { Id = id, Embedding = BoundedVector(id) }));
                live.UnionWith(Enumerable.Range(5001, 1000));
                AssertAllocatedVectorLinks(db, 5000);
                db.Checkpoint();
                var target = BoundedVector(2);
                docs.Query().TopKNear(x => x.Embedding, target, 25).ToArray().Select(x => x.Id).Should()
                    .Equal(live.OrderBy(id => Math.Abs(id - 2)).Take(25));
                docs.Query().WhereNear(x => x.Embedding, target, double.MaxValue).ToArray()
                    .Select(x => x.Id).Should().BeEquivalentTo(live);
                docs.Count().Should().Be(5000);
                for (var id = 1002; id <= 2001; id++)
                {
                    docs.Delete(id).Should().BeTrue();
                    live.Remove(id);
                }
                AssertAllocatedVectorLinks(db, 5000);
                Console.WriteLine("hnswd_counts deletes=2000 inserts=1000 live=4000 allocated=5000 tombstones=2000");
                docs.DeleteAll().Should().Be(4000);
                docs.Count().Should().Be(0);
                docs.Query().TopKNear(x => x.Embedding, target, 25).ToArray().Should().BeEmpty();
                retainedAtEmpty = InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
                    snapshot.GetVectorPages().Sum(id => (int)snapshot.GetPage<VectorIndexPage>(id).ItemsCount));
                Console.WriteLine($"hnswd_counts live=0 allocated={retainedAtEmpty} deferred={RepairDeferred(db)}");
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                var docs = db.GetCollection<VectorDocument>("docs");
                AssertAllocatedVectorLinks(db, retainedAtEmpty);
                docs.Insert(new VectorDocument { Id = 2, Embedding = BoundedVector(-2) });
                docs.Query().TopKNear(x => x.Embedding, BoundedVector(-2), 1).ToArray().Single().Embedding.Should().Equal(BoundedVector(-2));
                db.Rebuild();
                RepairDeferred(db).Should().BeFalse();
                AssertAllocatedVectorLinks(db, 1);
                docs.DropIndex("embedding_idx").Should().BeTrue();
                AllocatedVectorPages(db, "docs").Should().Be(0);
                db.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("untouched");
                db.Checkpoint();
            }
            retainedAtEmpty.Should().Be(0, "a fully deleted collection must not require Rebuild to release all 5,000 stale vector nodes");
        }
    }
}
