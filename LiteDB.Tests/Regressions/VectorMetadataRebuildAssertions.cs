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
            var pageOffset = checked((int)collectionId * PAGE_SIZE);
            var collectionPage = new CollectionPage(new PageBuffer(bytes, pageOffset, 0));
            var metadata = collectionPage.GetVectorIndexMetadata("coalesced");
            metadata.Should().NotBeNull();
            metadata.Root.IsEmpty.Should().BeFalse("the healthy graph must retain its entry point");
            var nodes = new List<VectorIndexNode>();
            for (var start = PAGE_SIZE; start < bytes.Length; start += PAGE_SIZE)
            {
                if (bytes[start + BasePage.P_PAGE_TYPE] != (byte)PageType.VectorIndex) continue;
                var page = new VectorIndexPage(new PageBuffer(bytes, start, 0));
                if (page.ColID == collectionId) nodes.AddRange(page.GetNodes());
            }
            nodes.Should().HaveCount(documents.Length);
            nodes.Select(x => x.Position).Should().Contain(metadata.Root);
            nodes.Select(x => x.DataBlock).Should().OnlyHaveUniqueItems();
            foreach (var document in documents)
            {
                document.RawId.IsEmpty.Should().BeFalse();
                var node = nodes.Single(x => x.DataBlock == document.RawId);
                var vector = document["Embedding"].IsNull ? new[] { 0f, 0f } : document["Embedding"].AsVector.ToArray();
                node.ReadVector().Should().Equal(vector, "every surviving document must retain its computed vector entry");
            }
            documents.Select(document => nodes.Single(node => node.DataBlock == document.RawId).LevelCount)
                .Should().Equal(VectorMetadataRebuildLevels.Sequence, "rebuild must use the pinned level sequence");
            var byPosition = nodes.ToDictionary(x => x.Position);
            var reachable = new HashSet<PageAddress>();
            var pending = new Queue<PageAddress>();
            pending.Enqueue(metadata.Root);
            while (pending.Count > 0)
            {
                var address = pending.Dequeue();
                if (!reachable.Add(address)) continue;
                var node = byPosition[address];
                for (var level = 0; level < node.LevelCount; level++)
                foreach (var neighbor in node.GetNeighbors(level)) pending.Enqueue(neighbor);
            }
            reachable.Should().BeEquivalentTo(byPosition.Keys, "this controlled graph must connect every stored node");

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

            // A bounded TopKNear executes graph search, unlike the unbounded exact scan.
            // Exact recall is asserted only for this controlled graph, never random levels.
            foreach (var k in new[] { 5, 20 })
            {
                var ann = collection.Query().TopKNear(
                    BsonExpression.Create("COALESCE($.Embedding, [0, 0])"), target, k);
                var plan = ann.GetPlan();
                plan["index"]["name"].AsString.Should().Be("coalesced");
                plan["index"]["mode"].AsString.Should().Be("VECTOR INDEX SEARCH");
                plan["orderBy"].IsNull.Should().BeTrue("bounded graph search supplies distance order without scan sorting");
                var candidates = oracle.Take(k).ToArray();
                var results = ann.WithScore().ToArray();
                results.Select(x => x.Document["_id"].AsInt32).Should().Equal(candidates.Select(x => x.Id));
                for (var i = 0; i < results.Length; i++)
                {
                    results[i].Metric.Should().Be(VectorDistanceMetric.Cosine);
                    results[i].Score.Should().HaveValue().And.BeApproximately(candidates[i].Distance, 1e-6);
                }
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
