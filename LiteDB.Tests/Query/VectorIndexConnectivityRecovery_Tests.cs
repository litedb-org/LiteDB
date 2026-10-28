using System;
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
        [Fact]
        public void Graph_mutations_rollback_and_committed_wal_recover_complete_documents_and_recall()
        {
            using var file = new TempFile();
            var original = Enumerable.Range(1, 24).Select(i => new VectorDocument
                { Id = i, Embedding = new[] { 1f, (float)i } }).ToArray();
            using (var db = new LiteDatabase(file.Filename))
            {
                var docs = db.GetCollection<VectorDocument>("docs");
                docs.Insert(original);
                docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(2));
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
                db.Checkpoint();
                db.CheckpointSize = 0;
                db.BeginTrans().Should().BeTrue();
                MutateConnectivityDocuments(db);
                AssertConnectivity(db, 19);
                db.Rollback().Should().BeTrue();
                docs.FindAll().Should().BeEquivalentTo(original);
                AssertConnectivity(db, 24);
                db.BeginTrans().Should().BeTrue();
                MutateConnectivityDocuments(db);
                db.Commit().Should().BeTrue();
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var db = new LiteDatabase(file.Filename);
                AssertConnectivityState(db, changed: true);
                db.Checkpoint();
            }
        }

        [Fact]
        public void Lost_and_torn_graph_wal_writes_recover_whole_transactions_twice()
        {
            // Discover the finite physical-write count of this graph transaction, then
            // cut each write with either no new durable bytes or a torn durable prefix.
            VectorIndexService.LevelRandomFactory = () => new Random(71);
            try
            {
                var writes = RunGraphPowerCut(0, false);
                writes.Should().BeGreaterThan(0);
                for (var cut = 1; cut <= writes; cut++)
                {
                    RunGraphPowerCut(cut, false).Should().Be(cut);
                    RunGraphPowerCut(cut, true).Should().Be(cut);
                }
            }
            finally { VectorIndexService.LevelRandomFactory = null; }
        }

        private static int RunGraphPowerCut(int cut, bool torn)
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
            File.WriteAllBytes(wal, cut == 0 ? log.DurableBytes : torn ? log.DurableBytes : baselineLog);
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

        private static void MutateConnectivityDocuments(LiteDatabase db)
        {
            var docs = db.GetCollection<VectorDocument>("docs");
            foreach (var id in Enumerable.Range(1, 10)) docs.Delete(id).Should().BeTrue();
            foreach (var id in Enumerable.Range(11, 5))
                docs.Update(new VectorDocument { Id = id, Embedding = new[] { 2f, (float)-id } }).Should().BeTrue();
            docs.Insert(Enumerable.Range(25, 5).Select(i => new VectorDocument { Id = i, Embedding = new[] { 3f, (float)i } }));
        }

        private static void AssertConnectivityState(LiteDatabase db, bool changed)
        {
            var expected = Enumerable.Range(changed ? 11 : 1, changed ? 19 : 24).Select(id => new VectorDocument
            {
                Id = id,
                Embedding = changed && id <= 15 ? new[] { 2f, (float)-id }
                    : changed && id >= 25 ? new[] { 3f, (float)id } : new[] { 1f, (float)id }
            }).ToArray();
            var docs = db.GetCollection<VectorDocument>("docs");
            docs.FindAll().Should().BeEquivalentTo(expected);
            AssertConnectivity(db, expected.Length);
            var target = new[] { 1f, 7.25f };
            docs.Query().TopKNear(x => x.Embedding, target, 24).ToArray().Select(x => x.Id).Should()
                .Equal(ComputeExpectedRanking(expected, target, VectorDistanceMetric.Cosine).Select(x => x.Id));
            db.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("untouched");
        }

        [Fact]
        public void Existing_disconnected_graph_reads_unchanged_and_rebuild_restores_recall()
        {
            using var file = new TempFile();
            using (var resource = typeof(VectorIndex_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.HnswDisconnected_dev_b3c440623.zip"))
            using (var archive = new ZipArchive(resource))
            using (var input = archive.GetEntry("vectors.db").Open())
            using (var output = File.Create(file.Filename)) input.CopyTo(output);
            var original = File.ReadAllBytes(file.Filename);
            BsonDocument[] expected;
            var expression = BsonExpression.Create("COALESCE($.Embedding, [0, 0])");
            var target = new[] { 1f, 7f };
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true }))
            {
                expected = db.GetCollection("computed").FindAll().ToArray();
                expected.Length.Should().Be(20);
                db.GetCollection("computed").Query().TopKNear(expression, target, 20).ToArray()
                    .Select(x => x["_id"].AsInt32).Should().NotContain(7);
            }
            File.ReadAllBytes(file.Filename).Should().Equal(original);
            using (var db = new LiteDatabase(file.Filename)) db.Rebuild();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var db = new LiteDatabase(file.Filename);
                var docs = db.GetCollection("computed");
                docs.FindAll().Should().BeEquivalentTo(expected);
                var query = docs.Query().TopKNear(expression, target, 20);
                query.GetPlan()["index"]["name"].AsString.Should().Be("coalesced");
                query.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(
                    docs.Query().WhereNear(expression, target, double.MaxValue).ToArray().Select(x => x["_id"].AsInt32));
            }
        }
    }
}
