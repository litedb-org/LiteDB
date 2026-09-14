using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using FluentAssertions;

using LiteDB.Engine;
using LiteDB.Vector;

using Xunit;

namespace LiteDB.Tests.Issues
{
    // Retain the real-file scenario from CI commit 21d8e2103496d01eff0caeee2d5133908e793587.
    // The upstream test later switched to MemoryStream; delegating to it lost disk coverage.
    public class Issue2712_Tests
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

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void File_backed_updated_vectors_preserve_graph_payloads_and_find_themselves(int repetition)
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

                visited.Should().HaveCount(documents.Count, "every acknowledged vector must remain reachable");
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
#if NETFRAMEWORK
                var expectedBits = BitConverter.ToInt32(BitConverter.GetBytes(expected[i]), 0);
                var actualBits = BitConverter.ToInt32(BitConverter.GetBytes(actual[i]), 0);
                if (expectedBits != actualBits)
                {
                    return false;
                }
#else
                if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i]))
                {
                    return false;
                }
#endif
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
