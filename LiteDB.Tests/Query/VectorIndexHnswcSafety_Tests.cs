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
        [InlineData(VectorDistanceMetric.Cosine, false)]
        [InlineData(VectorDistanceMetric.Cosine, true)]
        [InlineData(VectorDistanceMetric.Euclidean, false)]
        [InlineData(VectorDistanceMetric.Euclidean, true)]
        [InlineData(VectorDistanceMetric.DotProduct, false)]
        [InlineData(VectorDistanceMetric.DotProduct, true)]
        public void Review2_hnswc_exact_fallback_file_compositions(VectorDistanceMetric metric, bool encrypted)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Password = encrypted ? "review2" : null };
            var model = Enumerable.Range(1, 90).Select(id => new VectorDocument
            {
                Id = id, Embedding = new[] { (float)(id % 7 - 3), (float)(id % 5 - 2), 1f }, Flag = id % 3 == 1
            }).ToArray();
            using (var db = new LiteDatabase(connection))
            {
                var docs = db.GetCollection<VectorDocument>("docs");
                docs.Insert(model);
                docs.EnsureIndex("embedding_idx", "$.Embedding", new VectorIndexOptions(3, metric));
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
                db.Checkpoint();
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var db = new LiteDatabase(connection);
                var docs = db.GetCollection<VectorDocument>("docs");
                var target = new[] { 1f, -2f, 1f };
                var threshold = metric == VectorDistanceMetric.DotProduct ? 0.0 : metric == VectorDistanceMetric.Cosine ? 1.1 : 4.0;
                var matching = model.Where(d => d.Flag && (metric == VectorDistanceMetric.DotProduct
                    ? ComputeReferenceMetrics(d.Embedding, target, metric).Similarity >= threshold
                    : ComputeReferenceMetrics(d.Embedding, target, metric).Distance <= threshold)).ToArray();
                void Verify(IEnumerable<VectorDocument> actual, IEnumerable<VectorDocument> candidates, int skip = 0)
                {
                    var rows = actual.ToArray();
                    var expected = ComputeExpectedRanking(candidates, target, metric).Skip(skip).Take(9).ToArray();
                    rows.Select(d => d.Id).Distinct().Count().Should().Be(rows.Length);
                    rows.Length.Should().Be(expected.Length);
                    for (var i = 0; i < rows.Length; i++)
                    {
                        candidates.Any(d => d.Id == rows[i].Id).Should().BeTrue();
                        ComputeReferenceMetrics(rows[i].Embedding, target, metric).Distance.Should().BeApproximately(expected[i].Distance, 1e-10);
                    }
                }
                Verify(docs.Query().TopKNear(x => x.Embedding, target, 9).ToArray(), model);
                Verify(docs.Query().Where(x => x.Flag).TopKNear(x => x.Embedding, target, 9).ToArray(), model.Where(d => d.Flag));
                var afterTopK = docs.Query();
                afterTopK.TopKNear(x => x.Embedding, target, 9);
                Verify(afterTopK.Where(x => x.Flag).ToArray(), model.Where(d => d.Flag));
                var whereThenTop = docs.Query().Where(x => x.Flag).WhereNear(x => x.Embedding, target, threshold);
                Verify(whereThenTop.TopKNear(x => x.Embedding, target, 9).ToArray(), matching);
                var topThenWhere = docs.Query();
                topThenWhere.TopKNear(x => x.Embedding, target, 9);
                Verify(topThenWhere.WhereNear(x => x.Embedding, target, threshold).Where(x => x.Flag).ToArray(), matching);
                Verify(docs.Query().Where(x => x.Flag).WhereNear(x => x.Embedding, target, threshold).Offset(2).Limit(9).ToArray(), matching, 2);
                var scored = docs.Query().Where(x => x.Flag).TopKNearWithScore(x => x.Embedding, target, 9).ToArray();
                Verify(scored.Select(h => h.Document), model.Where(d => d.Flag));
                foreach (var hit in scored)
                {
                    var score = ComputeReferenceMetrics(hit.Document.Embedding, target, metric);
                    hit.Metric.Should().Be(metric);
                    hit.Score.Should().BeApproximately(metric == VectorDistanceMetric.DotProduct ? score.Similarity : score.Distance, 1e-10);
                }
                RepairDeferred(db).Should().BeTrue();
                docs.Query().TopKNear(x => x.Embedding, target, 9).GetPlan()["index"]["mode"].AsString.Should().Be("VECTOR EXACT SCAN (REPAIR DEFERRED)");
                db.Checkpoint();
            }
        }

    }
}
