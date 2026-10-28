using System;
using System.Linq;
using System.Threading;
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
        public void Vector_census_ignores_foreign_unpublished_page_reservations()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                BuildBoundedRepairGraph(db, 65, arms: false);
                using var ready = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var peer = Task.Run(() =>
                {
                    db.BeginTrans().Should().BeTrue();
                    try
                    {
                        db.GetCollection("private").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = new string('x', 40000) });
                        ready.Set();
                        release.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
                    }
                    finally { db.Rollback().Should().BeTrue(); }
                });
                try
                {
                    ready.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
                    db.GetCollection<VectorDocument>("docs").Delete(2).Should().BeTrue();
                    RepairDeferred(db).Should().BeFalse();
                    AssertConnectivity(db, 64);
                    AssertAllocatedVectorLinks(db, 64);
                }
                finally { release.Set(); peer.GetAwaiter().GetResult(); }
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                AssertConnectivity(db, 64);
                db.GetCollection("private").Count().Should().Be(0);
            }
        }

        [Fact]
        public void Dropping_collection_releases_disconnected_vector_pages_and_external_payloads()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var docs = db.GetCollection<VectorDocument>("docs");
                docs.Insert(Enumerable.Range(1, 8).Select(id => new VectorDocument
                    { Id = id, Embedding = Enumerable.Repeat((float)id, 2048).ToArray() }));
                docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2048));
                docs.EnsureIndex("sibling", "$.Embedding", new VectorIndexOptions(2048, VectorDistanceMetric.Euclidean));
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
                var collectionId = db.Execute("SELECT $ FROM $dump WHERE $.collection = 'docs' AND $.pageType = 'Collection'")
                    .Single()["pageID"].AsInt32;
                AutoTransactionMethod.MakeGenericMethod(typeof(bool)).Invoke(EngineField.GetValue(db), new object[]
                {
                    new Func<TransactionService, bool>(transaction =>
                    {
                        var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
                        foreach (var item in snapshot.CollectionPage.GetVectorIndexes()) item.Metadata.Root = PageAddress.Empty;
                        snapshot.CollectionPage.IsDirty = true;
                        return true;
                    })
                });
                db.Checkpoint();
                db.DropCollection("docs").Should().BeTrue();
                var live = db.Execute("SELECT $ FROM $dump").ToArray();
                foreach (var page in live.Where(x => x["pageType"].AsString != "Header" && x["pageType"].AsString != "Empty"))
                    db.Execute($"SELECT $ FROM $dump({page["pageID"].AsInt32})").Single()["buffer"].AsBinary
                        .Skip(19).Take(4).ToArray().Should().NotEqual(BitConverter.GetBytes((uint)collectionId));
                db.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("untouched");
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                db.CollectionExists("docs").Should().BeFalse();
                db.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("untouched");
            }
        }
    }
}
