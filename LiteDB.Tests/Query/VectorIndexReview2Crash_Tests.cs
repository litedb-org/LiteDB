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
        [Fact]
        public void Review2_prefix_durable_forced_repair_recovery()
        {
            VectorIndexService.LevelRandomFactory = () => new Random(71);
            try
            {
                var writes = Review2RunPrefixPowerCut(0, false);
                writes.Should().BeGreaterThan(0);
                for (var cut = 1; cut <= writes; cut++)
                {
                    Review2RunPrefixPowerCut(cut, false).Should().Be(cut);
                    Review2RunPrefixPowerCut(cut, true).Should().Be(cut);
                }
            }
            finally { VectorIndexService.LevelRandomFactory = null; }
        }
        [Fact]
        public void Review2_forced_bridge_and_articulation_rollback()
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            var docs = db.GetCollection<VectorDocument>("docs");
            docs.Insert(Enumerable.Range(1, 25).Select(id => new VectorDocument { Id = id, Embedding = new[] { 1f, (float)id } }));
            docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));
            db.BeginTrans().Should().BeTrue();
            docs.Update(new VectorDocument { Id = 1, Embedding = new[] { 1f, 1f } }).Should().BeTrue();
            var center = InstallConnectivityStar(db, 25);
            db.Commit().Should().BeTrue();
            db.Checkpoint();
            var before = Review2GraphFingerprint(db);
            var documents = docs.FindAll().ToArray();
            db.BeginTrans().Should().BeTrue();
            docs.Insert(new VectorDocument { Id = 26, Embedding = new[] { 1f, 1f } });
            docs.Delete(center).Should().BeTrue();
            AssertConnectivity(db, 25);
            db.Rollback().Should().BeTrue();
            docs.FindAll().Should().BeEquivalentTo(documents);
            Review2GraphFingerprint(db).Should().Be(before);
            db.Checkpoint();
        }

        private static string Review2GraphFingerprint(LiteDatabase db) => InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
        {
            var seen = new System.Collections.Generic.HashSet<PageAddress>();
            var pending = new System.Collections.Generic.Stack<PageAddress>();
            var lines = new System.Collections.Generic.List<string>();
            pending.Push(metadata.Root);
            while (pending.Count > 0)
            {
                var address = pending.Pop();
                if (!seen.Add(address)) continue;
                var node = snapshot.GetPage<VectorIndexPage>(address.PageID).GetNode(address.Index);
                lines.Add(address + ":" + node.DataBlock + ":" + node.LevelCount + ":" +
                    string.Join(";", Enumerable.Range(0, 4).Select(level => string.Join(",", node.GetNeighbors(level)))));
                foreach (var neighbor in node.GetNeighbors(0)) pending.Push(neighbor);
            }
            return metadata.Root + "|" + string.Join("|", lines.OrderBy(line => line));
        });

        private static int Review2RunPrefixPowerCut(int cut, bool torn)
        {
            using var data = new PromotionPowerLossStream();
            using var log = new PromotionPowerLossStream();
            using var db = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = log, TransactionPageLimit = 1
            }));
            db.CheckpointSize = 0;
            var docs = db.GetCollection<VectorDocument>("docs");
            docs.Insert(Enumerable.Range(1, 24).Select(i => new VectorDocument
                { Id = i, Embedding = new[] { 1f, (float)i } }));
            docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));
            db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
            // Force the transaction under test to delete a real cut vertex (id 1),
            // so the WAL cuts cover the spanning-path repair, not just ordinary links.
            db.BeginTrans().Should().BeTrue();
            docs.Update(new VectorDocument { Id = 1, Embedding = new[] { 1f, 1f } }).Should().BeTrue();
            InstallConnectivityStar(db, 24).AsInt32.Should().Be(1);
            db.Commit().Should().BeTrue();
            AssertConnectivity(db, 24);
            db.Checkpoint();
            var baselineData = data.DurableBytes;
            var baselineLog = log.DurableBytes;
            var writes = 0;
            log.TearNextWrite = (buffer, offset, count) =>
            {
                if (++writes != cut) return;
                // Model every preceding accepted write reaching media before this cut.
                log.FlushToDisk();
                if (torn) log.TearWrite(buffer, offset, count, count / 2, false, shortFile: true);
                else log.PowerCut();
            };
            db.BeginTrans().Should().BeTrue();
            Action transaction = () => { MutateConnectivityDocuments(db); db.Commit().Should().BeTrue(); };
            if (cut == 0) transaction();
            else transaction.Should().Throw<IOException>().WithMessage("Modeled power loss");
            using var recovered = new TempFile();
            File.WriteAllBytes(recovered.Filename, baselineData);
            var wal = FileHelper.GetLogFile(recovered.Filename);
            File.WriteAllBytes(wal, log.DurableBytes);
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var reopened = new LiteDatabase(recovered.Filename);
                    AssertConnectivityState(reopened, changed: cut == 0);
                    reopened.Checkpoint();
                }
            }
            finally { File.Delete(wal); }
            return writes;
        }

    }
}
