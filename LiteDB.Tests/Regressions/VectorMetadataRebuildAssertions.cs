using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    internal static class VectorMetadataRebuildAssertions
    {
        internal static void ComputedIndex(LiteDatabase db, string filename, string password, BsonDocument[] expected)
        {
            var collection = db.GetCollection("computed");
            var documents = collection.FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
            collection.Count().Should().Be(20);
            db.GetCollection("$indexes").Find(Query.EQ("collection", "computed"))
                .Select(x => x["name"].AsString).Should().BeEquivalentTo(new[] { "_id", "coalesced" });
            documents.Should().Equal(expected, "rebuild must preserve every complete document, including null embeddings");
            collection.FindById(7).Equals(expected.Single(x => x["_id"].AsInt32 == 7)).Should().BeTrue();
            collection.FindById(15).Equals(expected.Single(x => x["_id"].AsInt32 == 15)).Should().BeTrue();

            // Inspect stored entries independently of ANN reachability and the exact document-scan path.
            db.Checkpoint();
            var bytes = VectorMetadataDamageFiles.Read(filename, password);
            var header = new HeaderPage(new PageBuffer(bytes, 0, 0));
            var collectionId = header.GetCollections().Single(x => x.Key == "computed").Value;
            var nodes = new List<VectorIndexNode>();
            for (var start = PAGE_SIZE; start < bytes.Length; start += PAGE_SIZE)
            {
                if (bytes[start + BasePage.P_PAGE_TYPE] != (byte)PageType.VectorIndex) continue;
                var page = new VectorIndexPage(new PageBuffer(bytes, start, 0));
                if (page.ColID == collectionId) nodes.AddRange(page.GetNodes());
            }
            nodes.Should().HaveCount(documents.Length);
            nodes.Select(x => x.DataBlock).Should().OnlyHaveUniqueItems();
            foreach (var document in documents)
            {
                document.RawId.IsEmpty.Should().BeFalse();
                var node = nodes.Single(x => x.DataBlock == document.RawId);
                var vector = document["Embedding"].IsNull ? new[] { 0f, 0f } : document["Embedding"].AsVector.ToArray();
                node.ReadVector().Should().Equal(vector, "every surviving document must retain its computed vector entry");
            }

            // Unbounded WhereNear uses exact scanning. Nonzero fixture vectors have distinct
            // cosine distances to this target; null embeddings coalesce to zero (undefined cosine).
            var target = new[] { 1f, 7.25f };
            var oracle = expected.Where(x => !x["Embedding"].IsNull).Select(x => new
            {
                Id = x["_id"].AsInt32,
                Distance = CosineDistance(x["Embedding"].AsVector.ToArray(), target)
            }).OrderBy(x => x.Distance).ToArray();
            oracle.Select(x => x.Distance).Should().OnlyHaveUniqueItems();
            var query = collection.Query().WhereNear(
                BsonExpression.Create("COALESCE($.Embedding, [0, 0])"), target, double.MaxValue);
            query.GetPlan()["index"]["name"].AsString.Should().Be("coalesced");
            var actual = query.WithScore().OrderBy(x => x.Score).ToArray();
            actual.Select(x => x.Document["_id"].AsInt32).Should().Equal(oracle.Select(x => x.Id));
            for (var i = 0; i < actual.Length; i++)
            {
                actual[i].Metric.Should().Be(VectorDistanceMetric.Cosine);
                actual[i].Score.Should().HaveValue().And.BeApproximately(oracle[i].Distance, 1e-6);
            }
        }

        private static double CosineDistance(float[] vector, float[] target)
        {
            double dot = 0, norm = 0, targetNorm = 0;
            for (var i = 0; i < vector.Length; i++)
            {
                dot += (double)vector[i] * target[i];
                norm += (double)vector[i] * vector[i];
                targetNorm += (double)target[i] * target[i];
            }
            return 1 - dot / Math.Sqrt(norm * targetNorm);
        }
    }
}
