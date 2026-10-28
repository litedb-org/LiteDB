using System;
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
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Articulation_mutation_never_rewrites_surviving_index_pages(bool update)
        {
            using var db = new LiteDatabase(":memory:");
            BuildBoundedRepairGraph(db, 2049, arms: true);
            db.BeginTrans().Should().BeTrue();
            var docs = db.GetCollection<VectorDocument>("docs");
            if (update) docs.Update(new VectorDocument { Id = 1, Embedding = BoundedVector(-1) }).Should().BeTrue();
            else docs.Delete(1).Should().BeTrue();
            InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
            {
                snapshot.LocalPages.OfType<VectorIndexPage>().Count(x => x.IsDirty).Should().BeLessThanOrEqualTo(16,
                    "repair must not rewrite every surviving node");
                return true;
            }).Should().BeTrue();
            db.Rollback().Should().BeTrue();
        }

        [Theory]
        [InlineData(1025, false)]
        [InlineData(1026, true)]
        public void Repair_budget_boundary_bridges_or_defers_without_whole_index_writes(int count, bool deferred)
        {
            using var db = new LiteDatabase(":memory:");
            BuildBoundedRepairGraph(db, count, arms: true);
            db.BeginTrans().Should().BeTrue();
            db.GetCollection<VectorDocument>("docs").Delete(1).Should().BeTrue();
            InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
            {
                snapshot.LocalPages.OfType<VectorIndexPage>().Count(x => x.IsDirty).Should().BeLessThanOrEqualTo(16);
                snapshot.CollectionPage.GetVectorIndexes().Single().Index.VectorRepairDeferred.Should().Be(deferred);
                return true;
            }).Should().BeTrue();
            db.Commit().Should().BeTrue();
            if (!deferred) AssertConnectivity(db, count - 1);
            else
            {
                // Deferred trees remain traversable for disposal even though their
                // document pointers are stale. Drop must not leak unreachable nodes.
                var pages = InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
                {
                    CountNodes(snapshot, metadata.Root).Should().Be(count);
                    return snapshot.LocalPages.OfType<VectorIndexPage>().Select(x => x.PageID).ToArray();
                });
                db.GetCollection<VectorDocument>("docs").DropIndex("embedding_idx").Should().BeTrue();
                db.GetCollection<VectorDocument>("docs").Count().Should().Be(count - 1);
                db.Checkpoint();
                AutoTransactionMethod.MakeGenericMethod(typeof(bool)).Invoke(EngineField.GetValue(db), new object[]
                {
                    new Func<TransactionService, bool>(transaction =>
                    {
                        var snapshot = transaction.CreateSnapshot(LockMode.Read, "$", false);
                        foreach (var page in pages) snapshot.GetPage<BasePage>(page).PageType.Should().Be(PageType.Empty);
                        return true;
                    })
                });
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Deferred_repair_signal_and_document_delete_recover_atomically_at_every_wal_cut(bool update)
        {
            var writes = RunDeferredPowerCut(0, false, update);
            writes.Should().BeGreaterThan(0);
            for (var cut = 1; cut <= writes; cut++)
            {
                RunDeferredPowerCut(cut, false, update).Should().Be(cut);
                RunDeferredPowerCut(cut, true, update).Should().Be(cut);
            }
        }

        private static int RunDeferredPowerCut(int cut, bool torn, bool update)
        {
            using var data = new PromotionPowerLossStream();
            using var log = new PromotionPowerLossStream();
            using var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }));
            db.CheckpointSize = 0;
            BuildBoundedRepairGraph(db, 2049, arms: true);
            db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
            db.Checkpoint();
            var baseline = data.DurableBytes;
            var writes = 0;
            log.TearNextWrite = (buffer, offset, count) =>
            {
                if (++writes != cut) return;
                log.FlushToDisk(); // All accepted earlier frames survive this cut.
                if (torn) log.TearWrite(buffer, offset, count, count / 2, false, shortFile: true);
                else log.PowerCut();
            };
            Action mutation = () =>
            {
                db.BeginTrans().Should().BeTrue();
                MutateDeferredGraph(db, update);
                db.Commit().Should().BeTrue();
            };
            if (cut == 0) mutation();
            else mutation.Should().Throw<IOException>().WithMessage("Modeled power loss");
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, baseline);
            var wal = FileHelper.GetLogFile(file.Filename);
            File.WriteAllBytes(wal, log.DurableBytes);
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var recovered = new LiteDatabase(file.Filename);
                    var docs = recovered.GetCollection<VectorDocument>("docs");
                    var first = cut == 0 && !update ? 2 : 1;
                    var stored = docs.FindAll().OrderBy(x => x.Id).ToArray();
                    stored.Select(x => x.Id).Should().Equal(Enumerable.Range(first, 2050 - first));
                    foreach (var document in stored)
                        document.Embedding.Should().Equal(BoundedVector(cut == 0 && update && document.Id == 1 ? -1 : document.Id));
                    if (cut == 0) AssertDeferredRepair(recovered, update);
                    else
                    {
                        AssertConnectivity(recovered, 2049);
                        docs.Query().TopKNear(x => x.Embedding, BoundedVector(1), 1).ToArray().Single().Id.Should().Be(1);
                    }
                    recovered.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("untouched");
                    recovered.Checkpoint();
                }
            }
            finally { File.Delete(wal); }
            return writes;
        }
    }
}
