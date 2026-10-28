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
        public void Retried_graph_and_cleared_signal_recover_atomically_at_every_wal_cut(bool update)
        {
            (byte[] Data, byte[] Log) baseline;
            using (var data = new PromotionPowerLossStream(AsymmetricFixture()))
            using (var log = new PromotionPowerLossStream())
            using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log })))
            {
                db.CheckpointSize = 0;
                db.GetCollection<VectorDocument>("docs").Delete(1).Should().BeTrue();
                RepairDeferred(db).Should().BeTrue();
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
                db.Checkpoint();
                baseline = (data.DurableBytes, log.DurableBytes);
            }
            var writes = RunRetryPowerCut(baseline, 0, false, update);
            writes.Should().BeGreaterThan(0);
            for (var cut = 1; cut <= writes; cut++)
            {
                RunRetryPowerCut(baseline, cut, false, update).Should().Be(cut);
                RunRetryPowerCut(baseline, cut, true, update).Should().Be(cut);
            }
        }

        private static int RunRetryPowerCut((byte[] Data, byte[] Log) baseline, int cut, bool torn, bool update)
        {
            using var data = new PromotionPowerLossStream(baseline.Data);
            using var log = new PromotionPowerLossStream(baseline.Log);
            using var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }));
            db.CheckpointSize = 0;
            var writes = 0;
            log.TearNextWrite = (buffer, offset, count) =>
            {
                if (++writes != cut) return;
                log.FlushToDisk();
                if (torn) log.TearWrite(buffer, offset, count, count / 2, false, shortFile: true);
                else log.PowerCut();
            };
            Action mutation = () => RetrySmallGraph(db.GetCollection<VectorDocument>("docs"), update);
            if (cut == 0) mutation();
            else mutation.Should().Throw<IOException>().WithMessage("Modeled power loss");
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
                    var first = cut == 0 && !update ? 3 : 2;
                    var stored = docs.FindAll().OrderBy(x => x.Id).ToArray();
                    stored.Select(x => x.Id).Should().Equal(Enumerable.Range(first, 74 - first));
                    foreach (var document in stored)
                        document.Embedding.Should().Equal(new[] { 1f, (float)(cut == 0 && update && document.Id == 2 ? -2 : document.Id) });
                    RepairDeferred(recovered).Should().Be(cut != 0);
                    var target = new[] { 1f, -2f };
                    docs.Query().TopKNear(x => x.Embedding, target, 73).ToArray().Select(x => x.Id).Should()
                        .Equal(ComputeExpectedRanking(stored, target, VectorDistanceMetric.Cosine).Select(x => x.Id));
                    if (cut == 0) AssertConnectivity(recovered, update ? 72 : 71);
                    recovered.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("untouched");
                    recovered.Checkpoint();
                }
            }
            finally { File.Delete(wal); }
            return writes;
        }
    }
}
