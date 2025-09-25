using FluentAssertions;
using LiteDB;
using LiteDB.Engine;
using LiteDB.Tests;
using MathNet.Numerics.LinearAlgebra;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;
using LiteDB.Tests.Utils;

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
        private static readonly MethodInfo ReadExternalVectorMethod = typeof(VectorIndexService).GetMethod("ReadExternalVector", BindingFlags.NonPublic | BindingFlags.Instance);

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

        private static float[] CreateVector(Random random, int dimensions)
        {
            var vector = new float[dimensions];
            var hasNonZero = false;

            for (var i = 0; i < dimensions; i++)
            {
                var value = (float)(random.NextDouble() * 2d - 1d);
                vector[i] = value;

                if (!hasNonZero && Math.Abs(value) > 1e-6f)
                {
                    hasNonZero = true;
                }
            }

            if (!hasNonZero)
            {
                vector[random.Next(dimensions)] = 1f;
            }

            return vector;
        }

        private static float[] ReadExternalVector(DataService dataService, PageAddress start, int dimensions, out int blocksRead)
        {
            var totalBytes = dimensions * sizeof(float);
            var bytesCopied = 0;
            var vector = new float[dimensions];
            blocksRead = 0;

            foreach (var slice in dataService.Read(start))
            {
                blocksRead++;

                if (bytesCopied >= totalBytes)
                {
                    break;
                }

                var available = Math.Min(slice.Count, totalBytes - bytesCopied);
                Buffer.BlockCopy(slice.Array, slice.Offset, vector, bytesCopied, available);
                bytesCopied += available;
            }

            if (bytesCopied != totalBytes)
            {
                throw new InvalidOperationException("Vector data block is incomplete.");
            }

            return vector;
        }

        [Fact]
        public void VectorIndex_ReclaimsVectorPagesAfterRebuild()
        {
            using var file = new TempFile();

            var maxDataBytes = DataService.MAX_DATA_BYTES_PER_PAGE;
            var baseFloats = maxDataBytes / sizeof(float);
            var dimensions = (baseFloats * 20) + 24;

            var documents = Enumerable.Range(1, 12)
                .Select(id => new VectorDocument
                {
                    Id = id,
                    Embedding = Enumerable.Range(0, dimensions)
                        .Select(i => (float)Math.Sin(id * 0.1 + i * 0.01))
                        .ToArray(),
                    Flag = id % 2 == 0
                })
                .ToList();

            var expectedVectors = documents.ToDictionary(x => x.Id, x => x.Embedding);

            var bsonDocuments = documents.Select(doc => new BsonDocument
            {
                ["_id"] = doc.Id,
                ["Embedding"] = new BsonVector(doc.Embedding),
                ["Flag"] = doc.Flag
            }).ToList();

            using (var setup = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
            {
                var collection = setup.GetCollection("vectors");

                collection.Insert(bsonDocuments);

                collection.EnsureIndex(
                    "embedding_idx",
                    BsonExpression.Create("$.Embedding"),
                    new VectorIndexOptions((ushort)dimensions, VectorDistanceMetric.Cosine));

                setup.Checkpoint();
            }

            using (var updater = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
            {
                var updateCollection = updater.GetCollection<VectorDocument>("vectors");

                var updatedDocuments = documents
                    .Select(doc => new VectorDocument
                    {
                        Id = doc.Id,
                        Embedding = doc.Embedding.Select((value, index) => (float)Math.Cos(value + index * 0.001)).ToArray(),
                        Flag = doc.Flag
                    })
                    .ToList();

                foreach (var doc in updatedDocuments)
                {
                    updateCollection.Update(doc).Should().BeTrue();
                }

                updater.Checkpoint();

                documents = updatedDocuments;
                expectedVectors = documents.ToDictionary(x => x.Id, x => x.Embedding);
            }

            using var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename);
            var reopened = db.GetCollection<VectorDocument>("vectors");

            var storedDocuments = reopened.FindAll().ToList();

            storedDocuments.Should().HaveCount(documents.Count);

            foreach (var doc in storedDocuments)
            {
                var expected = expectedVectors[doc.Id];
                VectorsMatch(expected, doc.Embedding).Should().BeTrue();
            }

            var inspection = InspectVectorIndex(db, "vectors", (snapshot, collation, metadata) =>
            {
                var dataService = new DataService(snapshot, uint.MaxValue);
                var visited = new HashSet<PageAddress>();
                var queue = new Queue<PageAddress>();
                var mismatched = new List<int>();
                var multiBlock = 0;
                var externalNodes = 0;
                var nodes = 0;

                queue.Enqueue(metadata.Root);

                while (queue.Count > 0)
                {
                    var address = queue.Dequeue();

                    if (!visited.Add(address) || address.IsEmpty)
                    {
                        continue;
                    }

                    var node = snapshot.GetPage<VectorIndexPage>(address.PageID).GetNode(address.Index);
                    nodes++;

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

                    using var reader = new BufferReader(dataService.Read(node.DataBlock));
                    var document = reader.ReadDocument().GetValue();
                    var mapped = BsonMapper.Global.ToObject<VectorDocument>(document);

                    float[] actual;

                    if (node.HasInlineVector)
                    {
                        actual = node.ReadVector();
                    }
                    else
                    {
                        externalNodes++;
                        actual = ReadExternalVector(dataService, node.ExternalVector, metadata.Dimensions, out var blocksRead);

                        if (blocksRead > 1)
                        {
                            multiBlock++;
                        }
                    }

                    var expected = expectedVectors[mapped.Id];

                    if (!VectorsMatch(expected, actual))
                    {
                        mismatched.Add(mapped.Id);
                    }
                }

                return new
                {
                    NodeCount = nodes,
                    ExternalNodes = externalNodes,
                    MultiBlockNodes = multiBlock,
                    Mismatched = mismatched
                };
            });

            inspection.NodeCount.Should().Be(documents.Count);
            inspection.ExternalNodes.Should().BeGreaterThan(0, "vectors large enough should be stored externally");
            inspection.MultiBlockNodes.Should().BeGreaterThan(0, "vectors should span multiple data blocks");
            inspection.Mismatched.Should().BeEmpty();

            var target = documents[0].Embedding;
            var expectedTop = ComputeExpectedRanking(documents, target, VectorDistanceMetric.Cosine, 8);
            var results = reopened.Query()
                .TopKNear(x => x.Embedding, target, expectedTop.Count)
                .ToArray();

            results.Should().HaveCount(expectedTop.Count);
            results.Select(x => x.Id).Should().Equal(expectedTop.Select(x => x.Id));
        }

        private static (double Distance, double Similarity) ComputeReferenceMetrics(float[] candidate, float[] target, VectorDistanceMetric metric)
        {
            var builder = Vector<double>.Build;
            var candidateVector = builder.DenseOfEnumerable(candidate.Select(v => (double)v));
            var targetVector = builder.DenseOfEnumerable(target.Select(v => (double)v));

            switch (metric)
            {
                case VectorDistanceMetric.Cosine:
                    var candidateNorm = candidateVector.L2Norm();
                    var targetNorm = targetVector.L2Norm();

                    if (candidateNorm == 0d || targetNorm == 0d)
                    {
                        return (double.NaN, double.NaN);
                    }

                    var cosineSimilarity = candidateVector.DotProduct(targetVector) / (candidateNorm * targetNorm);
                    return (1d - cosineSimilarity, double.NaN);

                case VectorDistanceMetric.Euclidean:
                    return ((candidateVector - targetVector).L2Norm(), double.NaN);

                case VectorDistanceMetric.DotProduct:
                    var dot = candidateVector.DotProduct(targetVector);
                    return (-dot, dot);

                default:
                    throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
            }
        }

        private static List<(int Id, double Distance, double Similarity)> ComputeExpectedRanking(
            IEnumerable<VectorDocument> documents,
            float[] target,
            VectorDistanceMetric metric,
            int? limit = null)
        {
            var ordered = documents
                .Select(doc =>
                {
                    var (distance, similarity) = ComputeReferenceMetrics(doc.Embedding, target, metric);
                    return (doc.Id, Distance: distance, Similarity: similarity);
                })
                .Where(result => metric == VectorDistanceMetric.DotProduct
                    ? !double.IsNaN(result.Similarity)
                    : !double.IsNaN(result.Distance))
                .OrderBy(result => metric == VectorDistanceMetric.DotProduct ? -result.Similarity : result.Distance)
                .ThenBy(result => result.Id)
                .ToList();

            if (limit.HasValue)
            {
                ordered = ordered.Take(limit.Value).ToList();
            }

            return ordered;
        }

        [Fact]
        public void VectorIndex_HandlesVectorsSpanningMultipleDataBlocks_ReadsBack()
        {
            using var tempFile = new TempFile();
            using var db = new LiteDatabase(tempFile.Filename);
            var collection = db.GetCollection<VectorDocument>("vectors");

            var dimensions = (DataService.MAX_DATA_BYTES_PER_PAGE / sizeof(float)) + 128;
            var documents = Enumerable.Range(0, 12)
                .Select(i => new VectorDocument
                {
                    Id = i + 1,
                    Embedding = Enumerable.Range(0, dimensions)
                        .Select(component => (float)Math.Sin((i + 1) * (component + 1) * 0.0009765625d))
                        .ToArray(),
                    Flag = (i & 1) == 0
                })
                .ToList();

            collection.Insert(documents);

            collection.EnsureIndex(
                "embedding_idx",
                BsonExpression.Create("$.Embedding"),
                new VectorIndexOptions((ushort)dimensions, VectorDistanceMetric.Cosine));

            var target = documents[3].Embedding;
            var matches = collection
                .Query()
                .WhereNear(x => x.Embedding, target, double.MaxValue)
                .ToList();

            matches.Should().NotBeEmpty();
            matches[0].Id.Should().Be(documents[3].Id);
            matches[0].Embedding.Length.Should().Be(dimensions);

            var expectedVectors = documents.ToDictionary(x => x.Id, x => x.Embedding);

            var observedVectors = InspectVectorIndex(
                db,
                "vectors",
                (snapshot, collation, metadata) =>
                {
                    var dataService = new DataService(snapshot, uint.MaxValue);
                    var visited = new HashSet<PageAddress>();
                    var queue = new Queue<PageAddress>();

                    if (!metadata.Root.IsEmpty)
                    {
                        queue.Enqueue(metadata.Root);
                    }

                    var vectors = new Dictionary<int, (bool Inline, float[] Vector)>();

                    while (queue.Count > 0)
                    {
                        var address = queue.Dequeue();
                        if (!visited.Add(address))
                        {
                            continue;
                        }

                        var node = snapshot.GetPage<VectorIndexPage>(address.PageID).GetNode(address.Index);

                        using var reader = new BufferReader(dataService.Read(node.DataBlock));
                        var document = reader.ReadDocument().GetValue();
                        var id = document["Id"].AsInt32;

                        float[] vector;

                        if (node.HasInlineVector)
                        {
                            vector = node.ReadVector();
                        }
                        else
                        {
                            vector = new float[metadata.Dimensions];
                            var totalBytes = vector.Length * sizeof(float);
                            var bytesCopied = 0;

                            foreach (var slice in dataService.Read(node.ExternalVector))
                            {
                                var chunk = Math.Min(slice.Count, totalBytes - bytesCopied);
                                Buffer.BlockCopy(slice.Array, slice.Offset, vector, bytesCopied, chunk);
                                bytesCopied += chunk;

                                if (bytesCopied >= totalBytes)
                                {
                                    break;
                                }
                            }

                            bytesCopied.Should().Be(totalBytes, "external vectors should span the expected number of bytes");
                        }

                        vectors[id] = (node.HasInlineVector, vector);

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

                    return vectors;
                });

            observedVectors.Should().NotBeNull();
            observedVectors.Should().NotBeEmpty();
            observedVectors.Values.Any(v => !v.Inline).Should().BeTrue("large vectors should spill to external data blocks");
            observedVectors.Keys.Should().BeEquivalentTo(expectedVectors.Keys);

            foreach (var pair in observedVectors)
            {
                var expectedVector = expectedVectors[pair.Key];
                var storedVector = pair.Value.Vector;

                storedVector.Length.Should().Be(expectedVector.Length);
                storedVector
                    .Select(BitConverter.SingleToInt32Bits)
                    .Should()
                    .Equal(expectedVector.Select(BitConverter.SingleToInt32Bits));
            }
        }


        [Fact]
        public void VectorIndex_HandlesVectorsSpanningMultipleDataBlocks_UpdateRegression()
        {
            using var file = new TempFile();
            var dimensions = 60000;

            var documents = new List<VectorDocument>();
            var expectedVectors = new Dictionary<int, float[]>();

            for (var id = 1; id <= 12; id++)
            {
                var vector = new float[dimensions];

                for (var i = 0; i < vector.Length; i++)
                {
                    vector[i] = (float)Math.Sin((id * 17 + i) * 0.15);
                }

                documents.Add(new VectorDocument { Id = id, Embedding = vector, Flag = id % 2 == 0 });
                expectedVectors[id] = vector.ToArray();
            }

            using (var setup = new LiteDatabase(file.Filename))
            {
                var collection = setup.GetCollection<VectorDocument>("vectors");

                collection.Insert(documents);

                collection.EnsureIndex(
                    "embedding_idx",
                    BsonExpression.Create("$.Embedding"),
                    new VectorIndexOptions((ushort)dimensions, VectorDistanceMetric.Cosine));

                setup.Checkpoint();
            }

            using (var db = new LiteDatabase(file.Filename))
            {
                var collection = db.GetCollection<VectorDocument>("vectors");

                var updatedVector = expectedVectors[6]
                    .Select(v => -v)
                    .ToArray();

                collection.Update(new VectorDocument { Id = 6, Embedding = updatedVector, Flag = true });

                expectedVectors[6] = updatedVector;
                documents[5].Embedding = updatedVector;

                var probe = updatedVector;

                var results = collection.Query()
                    .WhereNear(x => x.Embedding, probe, maxDistance: 0.001)
                    .ToArray();

                results.Should().ContainSingle();
                results[0].Id.Should().Be(6);

                var expectedResults = ComputeExpectedRanking(documents, probe, VectorDistanceMetric.Cosine, limit: 5)
                    .ToArray();

                var expectedRanking = expectedResults
                    .Select(x => x.Id)
                    .ToArray();

                var expectedDistances = expectedResults
                    .Select(x => x.Distance)
                    .ToArray();

                var topK = collection.Query()
                    .TopKNear(x => x.Embedding, probe, 5)
                    .ToArray();

                topK.Select(x => x.Id).Should().Equal(expectedRanking);

                InspectVectorIndex(db, "vectors", (snapshot, collation, metadata) =>
                {
                    metadata.Dimensions.Should().Be((ushort)dimensions);
                    metadata.Root.IsEmpty.Should().BeFalse();

                    var dataService = new DataService(snapshot, uint.MaxValue);
                    var vectorService = new DataService(snapshot, uint.MaxValue);
                    var visited = new HashSet<PageAddress>();
                    var queue = new Queue<PageAddress>();
                    queue.Enqueue(metadata.Root);

                    var service = new VectorIndexService(snapshot, collation);
                    var matches = service.Search(metadata, probe, double.MaxValue, 5).ToArray();

                    matches.Select(x => x.Document.TryGetValue("Id", out var explicitId)
                            ? explicitId.AsInt32
                            : x.Document["_id"].AsInt32)
                        .Should().Equal(expectedRanking);

                    matches.Select(x => x.Distance)
                        .Should().HaveCount(expectedDistances.Length);

                    for (var i = 0; i < expectedDistances.Length; i++)
                    {
                        matches[i].Distance.Should().BeApproximately(expectedDistances[i], 1e-6);
                    }

                    while (queue.Count > 0)
                    {
                        var address = queue.Dequeue();

                        if (!visited.Add(address) || address.IsEmpty)
                        {
                            continue;
                        }

                        var page = snapshot.GetPage<VectorIndexPage>(address.PageID);
                        var node = page.GetNode(address.Index);

                        node.HasInlineVector.Should().BeFalse("large vectors should spill into external data blocks");

                        using var reader = new BufferReader(dataService.Read(node.DataBlock));
                        var document = reader.ReadDocument().GetValue();

                        var idValue = document.TryGetValue("Id", out var explicitId)
                            ? explicitId
                            : document["_id"];

                        var id = idValue.AsInt32;
                        expectedVectors.ContainsKey(id).Should().BeTrue();

                        var slices = vectorService.Read(node.ExternalVector).ToList();
                        slices.Count.Should().BeGreaterThan(1, "vectors larger than a single data page should span multiple data blocks");
                        slices.Sum(slice => slice.Count).Should().Be(metadata.Dimensions * sizeof(float));

                        var expected = expectedVectors[id];
                        var actual = (float[])ReadExternalVectorMethod.Invoke(service, new object[] { node, metadata });

                        actual.Length.Should().Be(expected.Length);

                        var hasMismatch = false;

                        for (var i = 0; i < expected.Length; i++)
                        {
                            var expectedBits = BitConverter.SingleToInt32Bits(expected[i]);
                            var actualBits = BitConverter.SingleToInt32Bits(actual[i]);

                            if (expectedBits != actualBits)
                            {
                                hasMismatch = true;
                                break;
                            }
                        }

                        hasMismatch.Should().BeFalse();

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

                    return 0;
                });
            }
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

            const int nearClusterSize = 64;
            const int farClusterSize = 64;

            var documents = new List<VectorDocument>();

            for (var i = 0; i < nearClusterSize; i++)
            {
                documents.Add(new VectorDocument
                {
                    Id = i + 1,
                    Embedding = new[] { 1f, i / 100f },
                    Flag = true
                });
            }

            for (var i = 0; i < farClusterSize; i++)
            {
                documents.Add(new VectorDocument
                {
                    Id = i + nearClusterSize + 1,
                    Embedding = new[] { -1f, 2f + i / 100f },
                    Flag = false
                });
            }

            collection.Insert(documents);
            collection.Count().Should().Be(documents.Count);

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
            stats.Total.Should().BeGreaterOrEqualTo(nearClusterSize);
            stats.Matches.Should().OnlyContain(id => id <= nearClusterSize);
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

        [Theory]
        [InlineData(VectorDistanceMetric.Cosine)]
        [InlineData(VectorDistanceMetric.Euclidean)]
        [InlineData(VectorDistanceMetric.DotProduct)]
        public void VectorDistance_Computation_MatchesMathNet(VectorDistanceMetric metric)
        {
            var random = new Random(1789);
            const int dimensions = 6;

            for (var i = 0; i < 20; i++)
            {
                var candidate = CreateVector(random, dimensions);
                var target = CreateVector(random, dimensions);

                var distance = VectorIndexService.ComputeDistance(candidate, target, metric, out var similarity);
                var (expectedDistance, expectedSimilarity) = ComputeReferenceMetrics(candidate, target, metric);

                if (double.IsNaN(expectedDistance))
                {
                    double.IsNaN(distance).Should().BeTrue();
                }
                else
                {
                    distance.Should().BeApproximately(expectedDistance, 1e-6);
                }

                if (double.IsNaN(expectedSimilarity))
                {
                    double.IsNaN(similarity).Should().BeTrue();
                }
                else
                {
                    similarity.Should().BeApproximately(expectedSimilarity, 1e-6);
                }
            }

            if (metric == VectorDistanceMetric.Cosine)
            {
                var zero = new float[dimensions];
                var other = CreateVector(random, dimensions);

                var distance = VectorIndexService.ComputeDistance(zero, other, metric, out var similarity);

                double.IsNaN(distance).Should().BeTrue();
                double.IsNaN(similarity).Should().BeTrue();
            }
        }

        [Theory]
        [InlineData(VectorDistanceMetric.Cosine)]
        [InlineData(VectorDistanceMetric.Euclidean)]
        [InlineData(VectorDistanceMetric.DotProduct)]
        public void VectorIndex_Search_MatchesReferenceRanking(VectorDistanceMetric metric)
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("vectors");

            var random = new Random(4242);
            const int dimensions = 6;

            var documents = Enumerable.Range(1, 32)
                .Select(i => new VectorDocument
                {
                    Id = i,
                    Embedding = CreateVector(random, dimensions),
                    Flag = i % 2 == 0
                })
                .ToList();

            collection.Insert(documents);

            collection.EnsureIndex(
                "embedding_idx",
                BsonExpression.Create("$.Embedding"),
                new VectorIndexOptions((ushort)dimensions, metric));

            var target = CreateVector(random, dimensions);
            foreach (var limit in new[] { 5, 12 })
            {
                var expectedTop = ComputeExpectedRanking(documents, target, metric, limit);

                var actual = InspectVectorIndex(db, "vectors", (snapshot, collation, metadata) =>
                {
                    var service = new VectorIndexService(snapshot, collation);
                    return service.Search(metadata, target, double.MaxValue, limit)
                        .Select(result =>
                        {
                            var mapped = BsonMapper.Global.ToObject<VectorDocument>(result.Document);
                            return (Id: mapped.Id, Score: result.Distance);
                        })
                        .ToList();
                });

                actual.Should().HaveCount(expectedTop.Count);

                for (var i = 0; i < expectedTop.Count; i++)
                {
                    actual[i].Id.Should().Be(expectedTop[i].Id);

                    if (metric == VectorDistanceMetric.DotProduct)
                    {
                        actual[i].Score.Should().BeApproximately(expectedTop[i].Similarity, 1e-6);
                    }
                    else
                    {
                        actual[i].Score.Should().BeApproximately(expectedTop[i].Distance, 1e-6);
                    }
                }
            }
        }

        [Theory]
        [InlineData(VectorDistanceMetric.Cosine)]
        [InlineData(VectorDistanceMetric.Euclidean)]
        [InlineData(VectorDistanceMetric.DotProduct)]
        public void WhereNear_MatchesReferenceOrdering(VectorDistanceMetric metric)
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("vectors");

            var random = new Random(9182);
            const int dimensions = 6;

            var documents = Enumerable.Range(1, 40)
                .Select(i => new VectorDocument
                {
                    Id = i,
                    Embedding = CreateVector(random, dimensions),
                    Flag = i % 3 == 0
                })
                .ToList();

            collection.Insert(documents);

            collection.EnsureIndex(
                "embedding_idx",
                BsonExpression.Create("$.Embedding"),
                new VectorIndexOptions((ushort)dimensions, metric));

            var target = CreateVector(random, dimensions);
            const int limit = 12;

            var query = collection.Query()
                .WhereNear(x => x.Embedding, target, double.MaxValue)
                .Limit(limit);

            var plan = query.GetPlan();
            plan["index"]["mode"].AsString.Should().Be("VECTOR INDEX SEARCH");

            var results = query.ToArray();

            results.Should().HaveCount(limit);

            var searchIds = InspectVectorIndex(db, "vectors", (snapshot, collation, metadata) =>
            {
                var service = new VectorIndexService(snapshot, collation);
                return service.Search(metadata, target, double.MaxValue, limit)
                    .Select(result => BsonMapper.Global.ToObject<VectorDocument>(result.Document).Id)
                    .ToArray();
            });

            results.Select(x => x.Id).Should().Equal(searchIds);
        }

        [Theory]
        [InlineData(VectorDistanceMetric.Cosine)]
        [InlineData(VectorDistanceMetric.Euclidean)]
        [InlineData(VectorDistanceMetric.DotProduct)]
        public void TopKNear_MatchesReferenceOrdering(VectorDistanceMetric metric)
        {
            using var db = new LiteDatabase(":memory:");
            var collection = db.GetCollection<VectorDocument>("vectors");

            var random = new Random(5461);
            const int dimensions = 6;

            var documents = Enumerable.Range(1, 48)
                .Select(i => new VectorDocument
                {
                    Id = i,
                    Embedding = CreateVector(random, dimensions),
                    Flag = i % 4 == 0
                })
                .ToList();

            collection.Insert(documents);

            collection.EnsureIndex(
                "embedding_idx",
                BsonExpression.Create("$.Embedding"),
                new VectorIndexOptions((ushort)dimensions, metric));

            var target = CreateVector(random, dimensions);
            const int limit = 7;
            var expected = ComputeExpectedRanking(documents, target, metric, limit);

            var results = collection.Query()
                .TopKNear(x => x.Embedding, target, limit)
                .ToArray();

            results.Should().HaveCount(expected.Count);
            results.Select(x => x.Id).Should().Equal(expected.Select(x => x.Id));
        }

        [Fact]
        public void VectorIndex_HandlesVectorsSpanningMultipleDataBlocks_PersistedUpdate()
        {
            using var file = new TempFile();

            var dimensions = ((DataService.MAX_DATA_BYTES_PER_PAGE / sizeof(float)) * 10) + 16;
            dimensions.Should().BeLessThan(ushort.MaxValue);

            var random = new Random(7321);
            var originalDocuments = Enumerable.Range(1, 12)
                .Select(i => new VectorDocument
                {
                    Id = i,
                    Embedding = CreateVector(random, dimensions),
                    Flag = i % 2 == 0
                })
                .ToList();

            var updateRandom = new Random(9813);
            var documents = originalDocuments
                .Select(doc => new VectorDocument
                {
                    Id = doc.Id,
                    Embedding = CreateVector(updateRandom, dimensions),
                    Flag = doc.Flag
                })
                .ToList();

            using (var setup = new LiteDatabase(file.Filename))
            {
                var setupCollection = setup.GetCollection<VectorDocument>("vectors");
                setupCollection.Insert(originalDocuments);

                var indexOptions = new VectorIndexOptions((ushort)dimensions, VectorDistanceMetric.Euclidean);
                setupCollection.EnsureIndex("embedding_idx", BsonExpression.Create("$.Embedding"), indexOptions);

                foreach (var doc in documents)
                {
                    setupCollection.Update(doc);
                }

                setup.Checkpoint();
            }

            using var db = new LiteDatabase(file.Filename);
            var collection = db.GetCollection<VectorDocument>("vectors");

            var (inlineDetected, mismatches) = InspectVectorIndex(db, "vectors", (snapshot, collation, metadata) =>
            {
                metadata.Should().NotBeNull();
                metadata.Dimensions.Should().Be((ushort)dimensions);

                var dataService = new DataService(snapshot, uint.MaxValue);
                var queue = new Queue<PageAddress>();
                var visited = new HashSet<PageAddress>();
                var collectedMismatches = new List<int>();
                var inlineSeen = false;

                if (!metadata.Root.IsEmpty)
                {
                    queue.Enqueue(metadata.Root);
                }

                while (queue.Count > 0)
                {
                    var address = queue.Dequeue();
                    if (!visited.Add(address))
                    {
                        continue;
                    }

                    var node = snapshot.GetPage<VectorIndexPage>(address.PageID).GetNode(address.Index);
                    inlineSeen |= node.HasInlineVector;

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

                    var storedVector = node.HasInlineVector
                        ? node.ReadVector()
                        : ReadExternalVector(dataService, node.ExternalVector, metadata.Dimensions);

                    using var reader = new BufferReader(dataService.Read(node.DataBlock));
                    var document = reader.ReadDocument().GetValue();
                    var typed = BsonMapper.Global.ToObject<VectorDocument>(document);

                    var expected = documents.Single(d => d.Id == typed.Id).Embedding;
                    if (!VectorsMatch(expected, storedVector))
                    {
                        collectedMismatches.Add(typed.Id);
                    }
                }

                return (inlineSeen, collectedMismatches);
            });

            Assert.False(inlineDetected);
            mismatches.Should().BeEmpty();

            foreach (var doc in documents)
            {
                var persisted = collection.FindById(doc.Id);
                Assert.NotNull(persisted);
                Assert.True(VectorsMatch(doc.Embedding, persisted.Embedding));

                var result = InspectVectorIndex(db, "vectors", (snapshot, collation, metadata) =>
                {
                    var service = new VectorIndexService(snapshot, collation);
                    return service.Search(metadata, doc.Embedding, double.MaxValue, 1).FirstOrDefault();
                });

                Assert.NotNull(result.Document);
                var mapped = BsonMapper.Global.ToObject<VectorDocument>(result.Document);
                mapped.Id.Should().Be(doc.Id);
                result.Distance.Should().BeApproximately(0d, 1e-6);
            }
        }

        private static bool VectorsMatch(float[] expected, float[] actual)
        {
            if (expected == null || actual == null)
            {
                return false;
            }

            if (expected.Length != actual.Length)
            {
                return false;
            }

            for (var i = 0; i < expected.Length; i++)
            {
                if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static float[] ReadExternalVector(DataService dataService, PageAddress start, int dimensions)
        {
            Assert.False(start.IsEmpty);
            Assert.True(dimensions > 0);

            var totalBytes = dimensions * sizeof(float);
            var vector = new float[dimensions];
            var bytesCopied = 0;

            foreach (var slice in dataService.Read(start))
            {
                if (bytesCopied >= totalBytes)
                {
                    break;
                }

                var available = Math.Min(slice.Count, totalBytes - bytesCopied);
                Assert.Equal(0, available % sizeof(float));

                Buffer.BlockCopy(slice.Array, slice.Offset, vector, bytesCopied, available);
                bytesCopied += available;
            }

            Assert.Equal(totalBytes, bytesCopied);

            return vector;
        }

    }
}
