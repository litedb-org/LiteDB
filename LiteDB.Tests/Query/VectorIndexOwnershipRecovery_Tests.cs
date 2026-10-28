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
        public void Disconnected_vector_cleanup_recovers_atomically_at_every_lost_and_torn_wal_write(bool drop)
        {
            (byte[] Data, byte[] Log) baseline;
            using (var data = new PromotionPowerLossStream())
            using (var log = new PromotionPowerLossStream())
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings
                { DataStream = data, LogStream = log, TransactionPageLimit = 1 })))
            {
                db.CheckpointSize = 0;
                var docs = db.GetCollection<VectorDocument>("docs");
                docs.Insert(Enumerable.Range(1, 8).Select(id => new VectorDocument
                    { Id = id, Embedding = Enumerable.Repeat((float)id, 2048).ToArray() }));
                docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2048));
                docs.EnsureIndex("sibling", "$.Embedding", new VectorIndexOptions(2048, VectorDistanceMetric.Euclidean));
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
                AutoTransactionMethod.MakeGenericMethod(typeof(bool)).Invoke(EngineField.GetValue(db), new object[]
                {
                    new Func<TransactionService, bool>(transaction =>
                    {
                        var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
                        var metadata = snapshot.CollectionPage.GetVectorIndexMetadata("embedding_idx");
                        metadata.Root = PageAddress.Empty;
                        snapshot.CollectionPage.GetCollectionIndex("embedding_idx").VectorRepairDeferred = true;
                        snapshot.CollectionPage.IsDirty = true;
                        return true;
                    })
                });
                db.Checkpoint();
                baseline = (data.DurableBytes, log.DurableBytes);
            }
            var previous = VectorIndexService.LevelRandomFactory;
            VectorIndexService.LevelRandomFactory = () => new Random(731);
            try
            {
                var writes = RunOwnershipCut(baseline, drop, 0, false);
                writes.Should().BeGreaterThan(0);
                for (var cut = 1; cut <= writes; cut++)
                {
                    RunOwnershipCut(baseline, drop, cut, false).Should().Be(cut);
                    RunOwnershipCut(baseline, drop, cut, true).Should().Be(cut);
                }
            }
            finally { VectorIndexService.LevelRandomFactory = previous; }
        }

        private static int RunOwnershipCut((byte[] Data, byte[] Log) baseline, bool drop, int cut, bool torn)
        {
            using var data = new PromotionPowerLossStream(baseline.Data);
            using var log = new PromotionPowerLossStream(baseline.Log);
            using var db = new LiteDatabase(new LiteEngine(new EngineSettings
                { DataStream = data, LogStream = log, TransactionPageLimit = 1 }));
            db.CheckpointSize = 0;
            var writes = 0;
            log.TearNextWrite = (buffer, offset, count) =>
            {
                if (++writes != cut) return;
                log.FlushToDisk();
                if (torn) log.TearWrite(buffer, offset, count, count / 2, false, shortFile: true);
                else log.PowerCut();
            };
            Action mutate = () =>
            {
                var docs = db.GetCollection<VectorDocument>("docs");
                if (drop) docs.DropIndex("embedding_idx").Should().BeTrue();
                else docs.Update(new VectorDocument { Id = 2, Embedding = Enumerable.Repeat(-2f, 2048).ToArray() }).Should().BeTrue();
            };
            if (cut == 0) mutate();
            else mutate.Should().Throw<IOException>().WithMessage("Modeled power loss");
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, baseline.Data);
            var wal = FileHelper.GetLogFile(file.Filename);
            File.WriteAllBytes(wal, log.DurableBytes);
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var recovered = new LiteDatabase(file.Filename);
                    var docs = recovered.GetCollection<VectorDocument>("docs");
                    var documents = docs.FindAll().OrderBy(x => x.Id).ToArray();
                    documents.Select(x => x.Id).Should().Equal(Enumerable.Range(1, 8));
                    foreach (var document in documents)
                        document.Embedding.Should().Equal(Enumerable.Repeat(
                            cut == 0 && !drop && document.Id == 2 ? -2f : document.Id, 2048));
                    var indexes = recovered.Execute("SELECT $ FROM $indexes WHERE $.collection = 'docs' AND $.indexType = 1").ToArray();
                    indexes.Should().HaveCount(cut == 0 && drop ? 1 : 2);
                    var retained = indexes.SingleOrDefault(x => x["name"].AsString == "embedding_idx");
                    if (retained != null) retained["vectorRepairDeferred"].AsBoolean.Should().Be(cut != 0);
                    var nodes = recovered.Execute("SELECT SUM(*.itemsCount) AS nodes FROM $dump WHERE $.collection = 'docs' AND $.pageType = 'VectorIndex'")
                        .Single()["nodes"].AsInt32;
                    nodes.Should().Be(cut == 0 && drop ? 8 : 16, "no orphan old nodes may survive successful cleanup");
                    recovered.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("untouched");
                    if (cut == 0)
                    {
                        docs.Query().TopKNear(x => x.Embedding, Enumerable.Repeat(2f, 2048).ToArray(), 8).ToArray()
                            .Select(x => x.Id).Should().BeEquivalentTo(Enumerable.Range(1, 8));
                        if (!drop) AssertAllocatedVectorLinks(recovered, 16);
                    }
                    recovered.Checkpoint();
                }
            }
            finally { File.Delete(wal); }
            return writes;
        }
    }
}
