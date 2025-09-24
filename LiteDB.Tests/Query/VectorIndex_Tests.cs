using FluentAssertions;
using LiteDB;
using LiteDB.Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class VectorIndex_Tests
    {
        private class VectorDocument
        {
            public int Id { get; set; }
            public float[] Embedding { get; set; }
            public bool Flag { get; set; }
        }

        private static readonly FieldInfo EngineField = typeof(LiteDatabase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo HeaderField = typeof(LiteEngine).GetField("_header", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo AutoTransactionMethod = typeof(LiteEngine).GetMethod("AutoTransaction", BindingFlags.NonPublic | BindingFlags.Instance);

        private static T InspectVectorIndex<T>(LiteDatabase db, string collection, Func<Snapshot, Collation, VectorIndexMetadata, T> selector)
        {
            var engine = (LiteEngine)EngineField.GetValue(db);
            var header = (HeaderPage)HeaderField.GetValue(engine);
            var collation = header.Pragmas.Collation;
            var method = AutoTransactionMethod.MakeGenericMethod(typeof(T));

            return (T)method.Invoke(engine, new object[]
            {
                new Func<TransactionService, T>(transaction =>
                {
                    var snapshot = transaction.CreateSnapshot(LockMode.Read, collection, false);
                    var metadata = snapshot.CollectionPage.GetVectorIndexMetadata("embedding_idx");

                    return metadata == null ? default : selector(snapshot, collation, metadata);
                })
            });
        }

        private static int CountNodes(Snapshot snapshot, PageAddress root)
        {
            if (root.IsEmpty)
            {
                return 0;
            }

            var visited = new HashSet<PageAddress>();
            var queue = new Queue<PageAddress>();
            queue.Enqueue(root);

            var count = 0;

            while (queue.Count > 0)
            {
                var address = queue.Dequeue();
                if (!visited.Add(address))
                {
                    continue;
                }

                var node = snapshot.GetPage<VectorIndexPage>(address.PageID).GetNode(address.Index);
                count++;

                for (var level = 0; level < node.LevelCount; level++)
                {
                    foreach (var neighbor in node.GetNeighbors(level))
                    {
                        if (!neighbor.IsEmpty)
                        {
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }

            return count;
        }

        [Fact]
        public void EnsureVectorIndex_CreatesAndReuses()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("vectors");

            collection.Insert(new[]
            {
                new VectorDocument { Id = 1, Embedding = new[] { 1f, 0f }, Flag = true },
                new VectorDocument { Id = 2, Embedding = new[] { 0f, 1f }, Flag = false }
            });

            var expression = BsonExpression.Create("$.Embedding");
            var options = new VectorIndexOptions(2, VectorDistanceMetric.Cosine);

            collection.EnsureIndex("embedding_idx", expression, options).Should().BeTrue();
            collection.EnsureIndex("embedding_idx", expression, options).Should().BeFalse();

            Action conflicting = () => collection.EnsureIndex("embedding_idx", expression, new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));

            conflicting.Should().Throw<LiteException>();
        }

        [Fact]
        public void EnsureVectorIndex_PreservesEnumerableExpressionsForVectorIndexes()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("documents");

            var resourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", "ingest-20250922-234735.json");
            var json = File.ReadAllText(resourcePath);

            using var parsed = JsonDocument.Parse(json);
            var embedding = parsed.RootElement
                .GetProperty("Embedding")
                .EnumerateArray()
                .Select(static value => value.GetSingle())
                .ToArray();

            var options = new VectorIndexOptions((ushort)embedding.Length, VectorDistanceMetric.Cosine);

            collection.EnsureIndex(x => x.Embedding, options);

            var document = new VectorDocument
            {
                Id = 1,
                Embedding = embedding,
                Flag = false
            };

            Action act = () => collection.Upsert(document);

            act.Should().NotThrow();

            var stored = collection.FindById(1);

            stored.Should().NotBeNull();
            stored.Embedding.Should().Equal(embedding);

            var storesInline = InspectVectorIndex(db, "documents", (snapshot, collation, metadata) =>
            {
                if (metadata.Root.IsEmpty)
                {
                    return true;
                }

                var page = snapshot.GetPage<VectorIndexPage>(metadata.Root.PageID);
                var node = page.GetNode(metadata.Root.Index);
                return node.HasInlineVector;
            });

            storesInline.Should().BeFalse();
        }

        [Fact]
        public void WhereNear_UsesVectorIndex_WhenAvailable()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("vectors");

            collection.Insert(new[]
            {
                new VectorDocument { Id = 1, Embedding = new[] { 1f, 0f }, Flag = true },
                new VectorDocument { Id = 2, Embedding = new[] { 0f, 1f }, Flag = false },
                new VectorDocument { Id = 3, Embedding = new[] { 1f, 1f }, Flag = false }
            });

            collection.EnsureIndex("embedding_idx", BsonExpression.Create("$.Embedding"), new VectorIndexOptions(2, VectorDistanceMetric.Cosine));

            var query = collection.Query()
                .WhereNear(x => x.Embedding, new[] { 1f, 0f }, maxDistance: 0.25);

            var plan = query.GetPlan();

            plan["index"]["mode"].AsString.Should().Be("VECTOR INDEX SEARCH");
            plan["index"]["expr"].AsString.Should().Be("$.Embedding");
            plan.ContainsKey("filters").Should().BeFalse();

            var results = query.ToArray();

            results.Select(x => x.Id).Should().Equal(new[] { 1 });
        }

        [Fact]
        public void WhereNear_FallsBack_WhenNoVectorIndexExists()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("vectors");

            collection.Insert(new[]
            {
                new VectorDocument { Id = 1, Embedding = new[] { 1f, 0f }, Flag = true },
                new VectorDocument { Id = 2, Embedding = new[] { 0f, 1f }, Flag = false }
            });

            var query = collection.Query()
                .WhereNear(x => x.Embedding, new[] { 1f, 0f }, maxDistance: 0.25);

            var plan = query.GetPlan();

            plan["index"]["mode"].AsString.Should().StartWith("FULL INDEX SCAN");
            plan["index"]["name"].AsString.Should().Be("_id");
            plan["filters"].AsArray.Count.Should().Be(1);

            var results = query.ToArray();

            results.Select(x => x.Id).Should().Equal(new[] { 1 });
        }

        [Fact]
        public void WhereNear_FallsBack_WhenDimensionMismatch()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("vectors");

            collection.Insert(new[]
            {
                new VectorDocument { Id = 1, Embedding = new[] { 1f, 0f, 0f }, Flag = true },
                new VectorDocument { Id = 2, Embedding = new[] { 0f, 1f, 0f }, Flag = false }
            });

            collection.EnsureIndex("embedding_idx", BsonExpression.Create("$.Embedding"), new VectorIndexOptions(3, VectorDistanceMetric.Cosine));

            var query = collection.Query()
                .WhereNear(x => x.Embedding, new[] { 1f, 0f }, maxDistance: 0.25);

            var plan = query.GetPlan();

            plan["index"]["mode"].AsString.Should().StartWith("FULL INDEX SCAN");
            plan["index"]["name"].AsString.Should().Be("_id");

            query.ToArray();
        }

        [Fact]
        public void TopKNear_UsesVectorIndex()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("vectors");

            collection.Insert(new[]
            {
                new VectorDocument { Id = 1, Embedding = new[] { 1f, 0f }, Flag = true },
                new VectorDocument { Id = 2, Embedding = new[] { 0f, 1f }, Flag = false },
                new VectorDocument { Id = 3, Embedding = new[] { 1f, 1f }, Flag = false }
            });

            collection.EnsureIndex("embedding_idx", BsonExpression.Create("$.Embedding"), new VectorIndexOptions(2, VectorDistanceMetric.Cosine));

            var query = collection.Query()
                .TopKNear(x => x.Embedding, new[] { 1f, 0f }, k: 2);

            var plan = query.GetPlan();

            plan["index"]["mode"].AsString.Should().Be("VECTOR INDEX SEARCH");
            plan.ContainsKey("orderBy").Should().BeFalse();

            var results = query.ToArray();

            results.Select(x => x.Id).Should().Equal(new[] { 1, 3 });
        }

        [Fact]
        public void OrderBy_VectorSimilarity_WithCompositeOrdering_UsesVectorIndex()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("vectors");

            collection.Insert(new[]
            {
                new VectorDocument { Id = 1, Embedding = new[] { 1f, 0f }, Flag = true },
                new VectorDocument { Id = 2, Embedding = new[] { 1f, 0f }, Flag = false },
                new VectorDocument { Id = 3, Embedding = new[] { 0f, 1f }, Flag = true }
            });

            collection.EnsureIndex(
                "embedding_idx",
                BsonExpression.Create("$.Embedding"),
                new VectorIndexOptions(2, VectorDistanceMetric.Cosine));

            var similarity = BsonExpression.Create("VECTOR_SIM($.Embedding, [1.0, 0.0])");

            var query = (LiteQueryable<VectorDocument>)collection.Query()
                .OrderBy(similarity, Query.Ascending)
                .ThenBy(x => x.Flag);

            var queryField = typeof(LiteQueryable<VectorDocument>).GetField("_query", BindingFlags.NonPublic | BindingFlags.Instance);
            var definition = (Query)queryField.GetValue(query);

            definition.OrderBy.Should().HaveCount(2);
            definition.OrderBy[0].Expression.Type.Should().Be(BsonExpressionType.VectorSim);

            definition.VectorField = "$.Embedding";
            definition.VectorTarget = new[] { 1f, 0f };
            definition.VectorMaxDistance = double.MaxValue;

            var plan = query.GetPlan();

            plan["index"]["mode"].AsString.Should().Be("VECTOR INDEX SEARCH");
            plan["index"]["expr"].AsString.Should().Be("$.Embedding");
            plan.ContainsKey("orderBy").Should().BeFalse();

            var results = query.ToArray();

            results.Should().HaveCount(3);
            results.Select(x => x.Id).Should().BeEquivalentTo(new[] { 1, 2, 3 });
        }

        [Fact]
        public void WhereNear_DotProductHonorsMinimumSimilarity()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("vectors");

            collection.Insert(new[]
            {
                new VectorDocument { Id = 1, Embedding = new[] { 1f, 0f } },
                new VectorDocument { Id = 2, Embedding = new[] { 0.6f, 0.6f } },
                new VectorDocument { Id = 3, Embedding = new[] { 0f, 1f } }
            });

            collection.EnsureIndex(
                "embedding_idx",
                BsonExpression.Create("$.Embedding"),
                new VectorIndexOptions(2, VectorDistanceMetric.DotProduct));

            var highThreshold = collection.Query()
                .WhereNear(x => x.Embedding, new[] { 1f, 0f }, maxDistance: 0.75)
                .ToArray();

            highThreshold.Select(x => x.Id).Should().Equal(new[] { 1 });

            var mediumThreshold = collection.Query()
                .WhereNear(x => x.Embedding, new[] { 1f, 0f }, maxDistance: 0.4)
                .ToArray();

            mediumThreshold.Select(x => x.Id).Should().Equal(new[] { 1, 2 });
        }

        [Fact]
        public void VectorIndex_Search_Prunes_Node_Visits()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("vectors");

            var documents = new List<VectorDocument>();

            for (var i = 0; i < 32; i++)
            {
                documents.Add(new VectorDocument
                {
                    Id = i + 1,
                    Embedding = new[] { 1f, i / 100f },
                    Flag = true
                });
            }

            for (var i = 0; i < 32; i++)
            {
                documents.Add(new VectorDocument
                {
                    Id = i + 33,
                    Embedding = new[] { -1f, 2f + i / 100f },
                    Flag = false
                });
            }

            collection.Insert(documents);

            collection.EnsureIndex(
                "embedding_idx",
                BsonExpression.Create("$.Embedding"),
                new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));

            var stats = InspectVectorIndex(
                db,
                "vectors",
                (snapshot, collation, metadata) =>
                {
                    var service = new VectorIndexService(snapshot, collation);
                    var matches = service.Search(metadata, new[] { 1f, 0f }, maxDistance: 0.25, limit: 5).ToList();
                    var total = CountNodes(snapshot, metadata.Root);

                    return (Visited: service.LastVisitedCount, Total: total, Matches: matches.Select(x => x.Document["Id"].AsInt32).ToArray());
                });

            stats.Total.Should().BeGreaterThan(stats.Visited);
            stats.Matches.Should().OnlyContain(id => id < 32);
        }

        [Fact]
        public void VectorIndex_PersistsNodes_WhenDocumentsChange()
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("vectors");

            collection.Insert(new[]
            {
                new VectorDocument { Id = 1, Embedding = new[] { 1f, 0f }, Flag = true },
                new VectorDocument { Id = 2, Embedding = new[] { 0f, 1f }, Flag = false },
                new VectorDocument { Id = 3, Embedding = new[] { 1f, 1f }, Flag = true }
            });

            collection.EnsureIndex("embedding_idx", BsonExpression.Create("$.Embedding"), new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));

            InspectVectorIndex(db, "vectors", (snapshot, collation, metadata) =>
            {
                metadata.Root.IsEmpty.Should().BeFalse();

                var service = new VectorIndexService(snapshot, collation);
                var target = new[] { 1f, 1f };
                service.Search(metadata, target, double.MaxValue, null).Count().Should().Be(3);

                return 0;
            });

            collection.Update(new VectorDocument { Id = 2, Embedding = new[] { 1f, 2f }, Flag = false });

            InspectVectorIndex(db, "vectors", (snapshot, collation, metadata) =>
            {
                var service = new VectorIndexService(snapshot, collation);
                var target = new[] { 1f, 1f };
                service.Search(metadata, target, double.MaxValue, null).Count().Should().Be(3);

                return 0;
            });

            collection.Update(new VectorDocument { Id = 3, Embedding = null, Flag = true });

            InspectVectorIndex(db, "vectors", (snapshot, collation, metadata) =>
            {
                var service = new VectorIndexService(snapshot, collation);
                var target = new[] { 1f, 1f };
                service.Search(metadata, target, double.MaxValue, null).Select(x => x.Document["_id"].AsInt32).Should().BeEquivalentTo(new[] { 1, 2 });

                return 0;
            });

            collection.DeleteMany(x => x.Id == 1);

            InspectVectorIndex(db, "vectors", (snapshot, collation, metadata) =>
            {
                var service = new VectorIndexService(snapshot, collation);
                var target = new[] { 1f, 1f };
                var results = service.Search(metadata, target, double.MaxValue, null).ToArray();

                results.Select(x => x.Document["_id"].AsInt32).Should().BeEquivalentTo(new[] { 2 });
                metadata.Root.IsEmpty.Should().BeFalse();

                return 0;
            });

            collection.DeleteAll();

            InspectVectorIndex(db, "vectors", (snapshot, collation, metadata) =>
            {
                var service = new VectorIndexService(snapshot, collation);
                var target = new[] { 1f, 1f };
                service.Search(metadata, target, double.MaxValue, null).Should().BeEmpty();
                metadata.Root.IsEmpty.Should().BeTrue();
                metadata.Reserved.Should().Be(uint.MaxValue);

                return 0;
            });
        }
    }
}
