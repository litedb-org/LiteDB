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
        private static byte[] AsymmetricFixture()
        {
            using var resource = typeof(VectorIndex_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.HnswAsymmetric_6b2ecd3af.zip");
            using var archive = new ZipArchive(resource);
            using var input = archive.GetEntry("vectors.db").Open();
            using var output = new MemoryStream();
            input.CopyTo(output);
            return output.ToArray();
        }

        private static bool RepairDeferred(LiteDatabase db, string collection = "docs") => db.Execute(
            "SELECT vectorRepairDeferred FROM $indexes WHERE collection = @0 AND name = 'embedding_idx'", collection).Single()["vectorRepairDeferred"].AsBoolean;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Persisted_asymmetric_graph_defers_before_mutation_and_retries_from_current_documents(bool update)
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, AsymmetricFixture());
            var original = File.ReadAllBytes(file.Filename);
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true }))
            {
                db.GetCollection<VectorDocument>("docs").Count().Should().Be(73);
                RepairDeferred(db).Should().BeFalse();
            }
            File.ReadAllBytes(file.Filename).Should().Equal(original);
            using (var db = new LiteDatabase(file.Filename))
            {
                var docs = db.GetCollection<VectorDocument>("docs");
                db.GetCollection("cold").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
                db.BeginTrans().Should().BeTrue();
                docs.Delete(1).Should().BeTrue();
                RepairDeferred(db).Should().BeTrue();
                InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
                {
                    snapshot.LocalPages.OfType<VectorIndexPage>().Count(x => x.IsDirty).Should().Be(0);
                    return true;
                }).Should().BeTrue();
                var target = new[] { 1f, 50f };
                docs.Query().TopKNear(x => x.Embedding, target, 12).ToArray().Select(x => x.Id).Should()
                    .Equal(ComputeExpectedRanking(docs.FindAll(), target, VectorDistanceMetric.Cosine, 12).Select(x => x.Id));
                db.Commit().Should().BeTrue();
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                var docs = db.GetCollection<VectorDocument>("docs");
                RepairDeferred(db).Should().BeTrue();
                db.BeginTrans().Should().BeTrue();
                RetrySmallGraph(docs, update);
                RepairDeferred(db).Should().BeFalse();
                AssertConnectivity(db, update ? 72 : 71);
                db.Rollback().Should().BeTrue();
                RepairDeferred(db).Should().BeTrue();
                docs.FindById(2).Embedding.Should().Equal(new[] { 1f, 2f });
                RetrySmallGraph(docs, update);
                RepairDeferred(db).Should().BeFalse();
                AssertConnectivity(db, update ? 72 : 71);
                docs.Query().TopKNear(x => x.Embedding, new[] { 1f, -2f }, 73).ToArray().Select(x => x.Id).Should()
                    .Equal(ComputeExpectedRanking(docs.FindAll(), new[] { 1f, -2f }, VectorDistanceMetric.Cosine).Select(x => x.Id));
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                RepairDeferred(db).Should().BeFalse();
                AssertConnectivity(db, update ? 72 : 71);
                db.GetCollection("cold").FindById(1)["value"].AsString.Should().Be("untouched");
                if (update) db.GetCollection<VectorDocument>("docs").FindById(2).Embedding.Should().Equal(new[] { 1f, -2f });
                else db.GetCollection<VectorDocument>("docs").FindById(2).Should().BeNull();
            }
        }

        private static void RetrySmallGraph(ILiteCollection<VectorDocument> docs, bool update)
        {
            if (update) docs.Update(new VectorDocument { Id = 2, Embedding = new[] { 1f, -2f } }).Should().BeTrue();
            else docs.Delete(2).Should().BeTrue();
        }

        [Theory]
        [InlineData(VectorDistanceMetric.Cosine)]
        [InlineData(VectorDistanceMetric.Euclidean)]
        [InlineData(VectorDistanceMetric.DotProduct)]
        public void Deferred_bounded_queries_use_metric_scores_filters_offsets_and_projections(VectorDistanceMetric metric)
        {
            using var db = new LiteDatabase(":memory:");
            var docs = db.GetCollection<VectorDocument>("docs");
            var random = new Random(9147);
            var documents = Enumerable.Range(1, 200).Select(id => new VectorDocument
            {
                Id = id, Embedding = Enumerable.Range(0, 4).Select(_ => (float)random.NextDouble()).ToArray()
            }).ToArray();
            docs.Insert(documents);
            docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(4, metric));
            AutoTransactionMethod.MakeGenericMethod(typeof(bool)).Invoke(EngineField.GetValue(db), new object[]
            {
                new Func<TransactionService, bool>(transaction =>
                {
                    var snapshot = transaction.CreateSnapshot(LockMode.Write, "docs", false);
                    snapshot.CollectionPage.GetVectorIndexes().Single().Index.VectorRepairDeferred = true;
                    snapshot.CollectionPage.IsDirty = true;
                    return true;
                })
            });
            var target = documents[31].Embedding;
            var expected = ComputeExpectedRanking(documents, target, metric).Select(x => x.Id).ToArray();
            docs.Query().TopKNear(x => x.Embedding, target, 12).GetPlan()["index"]["mode"].AsString.Should()
                .Be("VECTOR EXACT SCAN (REPAIR DEFERRED)");
            docs.Query().TopKNear(x => x.Embedding, target, 12).ToArray().Select(x => x.Id).Should().Equal(expected.Take(12));
            docs.Query().WhereNear(x => x.Embedding, target, double.MaxValue).Limit(12).ToArray().Select(x => x.Id)
                .Should().Equal(expected.Take(12));
            docs.Query().WhereNear(x => x.Embedding, target, double.MaxValue).Where(x => x.Id > 100)
                .Select(x => new { x.Id }).Offset(3).Limit(8).ToArray().Select(x => x.Id).Should().Equal(expected.Where(id => id > 100).Skip(3).Take(8));
            var ranked = ComputeExpectedRanking(documents, target, metric);
            var scored = docs.Query().TopKNearWithScore(x => x.Embedding, target, 12).ToArray();
            scored.Select(hit => hit.Document.Id).Should().Equal(expected.Take(12));
            for (var i = 0; i < scored.Length; i++)
            {
                scored[i].Metric.Should().Be(metric);
                scored[i].Score.Should().BeApproximately(metric == VectorDistanceMetric.DotProduct
                    ? ranked[i].Similarity : ranked[i].Distance, 1e-7);
            }
            var threshold = metric == VectorDistanceMetric.DotProduct
                ? (ranked[9].Similarity + ranked[10].Similarity) / 2
                : (ranked[9].Distance + ranked[10].Distance) / 2;
            docs.Query().WhereNear(x => x.Embedding, target, threshold).Limit(50).ToArray().Select(x => x.Id).Should()
                .Equal(expected.Take(10));
            RepairDeferred(db).Should().BeTrue();
        }
    }
}
