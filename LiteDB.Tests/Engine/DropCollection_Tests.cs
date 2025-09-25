using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB;
using LiteDB.Engine;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class DropCollection_Tests
    {
        private class VectorDocument
        {
            public int Id { get; set; }

            public float[] Embedding { get; set; }
        }

        private const string VectorIndexName = "embedding_idx";

        private static readonly FieldInfo EngineField = typeof(LiteDatabase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo AutoTransactionMethod = typeof(LiteEngine).GetMethod("AutoTransaction", BindingFlags.NonPublic | BindingFlags.Instance);

        [Fact]
        public void DropCollection()
        {
            using (var db = DatabaseFactory.Create())
            {
                db.GetCollectionNames().Should().NotContain("col");

                var col = db.GetCollection("col");

                col.Insert(new BsonDocument { ["a"] = 1 });

                db.GetCollectionNames().Should().Contain("col");

                db.DropCollection("col");

                db.GetCollectionNames().Should().NotContain("col");
            }
        }

        [Fact]
        public void InsertDropCollection()
        {
            using (var file = new TempFile())
            {
                using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
                {
                    var col = db.GetCollection("test");
                    col.Insert(new BsonDocument { ["_id"] = 1 });
                    db.DropCollection("test");
                    db.Rebuild();
                }

                using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
                {
                    var col = db.GetCollection("test");
                    col.Insert(new BsonDocument { ["_id"] = 1 });
                }
            }
        }

        [Fact]
        public void DropCollection_WithVectorIndex_ReclaimsPages_ByCounting()
        {
            using var file = new TempFile();

            const ushort dimensions = 6;

            using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
            {
                var collection = db.GetCollection("docs");

                collection.EnsureIndex(
                    VectorIndexName,
                    BsonExpression.Create("$.embedding"),
                    new VectorIndexOptions(dimensions, VectorDistanceMetric.Cosine));

                for (var i = 0; i < 8; i++)
                {
                    var embedding = new BsonArray(Enumerable.Range(0, dimensions)
                        .Select(j => new BsonValue(i + (j * 0.1))));

                    collection.Insert(new BsonDocument
                    {
                        ["_id"] = i + 1,
                        ["embedding"] = embedding
                    });
                }

                db.Checkpoint();
            }

            var beforeCounts = CountPagesByType(file.Filename);
            beforeCounts.TryGetValue(PageType.VectorIndex, out var vectorPagesBefore);
            vectorPagesBefore.Should().BeGreaterThan(0, "creating a vector index should allocate vector pages");

            var drop = () =>
            {
                using var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename);
                db.DropCollection("docs");
                db.Checkpoint();
            };

            drop.Should().NotThrow();

            var afterCounts = CountPagesByType(file.Filename);
            afterCounts.TryGetValue(PageType.VectorIndex, out var vectorPagesAfter);
            vectorPagesAfter.Should().BeLessThan(vectorPagesBefore, "dropping the collection should reclaim vector pages");
        }

        [Fact]
        public void DropCollection_WithVectorIndex_ReclaimsPages_SimpleVectors()
        {
            using var file = new TempFile();

            using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
            {
                var collection = db.GetCollection<VectorDocument>("vectors");
                var options = new VectorIndexOptions(8, VectorDistanceMetric.Cosine);

                collection.Insert(new List<VectorDocument>
                {
                    new VectorDocument { Id = 1, Embedding = new[] { 1f, 0.5f, -0.25f, 0.75f, 1.5f, -0.5f, 0.25f, -1f } },
                    new VectorDocument { Id = 2, Embedding = new[] { -0.5f, 0.25f, 0.75f, -1.5f, 1f, 0.5f, -0.25f, 0.125f } },
                    new VectorDocument { Id = 3, Embedding = new[] { 0.5f, -0.75f, 1.25f, 0.875f, -0.375f, 0.625f, -1.125f, 0.25f } }
                });

                collection.EnsureIndex(VectorIndexName, x => x.Embedding, options);

                db.Checkpoint();

                Action drop = () => db.DropCollection("vectors");

                drop.Should().NotThrow(
                    "dropping a collection with vector indexes should release vector index pages instead of treating them like skip-list indexes");

                db.Checkpoint();
            }

            using (var reopened = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
            {
                reopened.GetCollectionNames().Should().NotContain("vectors");
            }
        }

        [Fact]
        public void DropCollection_WithVectorIndex_ReclaimsTrackedPages()
        {
            using var file = new TempFile();

            HashSet<uint> vectorPages;
            HashSet<uint> vectorDataPages;

            var dimensions = (DataService.MAX_DATA_BYTES_PER_PAGE / sizeof(float)) + 64;
            dimensions.Should().BeLessThan(ushort.MaxValue);

            using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
            {
                var collection = db.GetCollection<VectorDocument>("docs");
                var documents = Enumerable.Range(1, 6)
                    .Select(i => new VectorDocument
                    {
                        Id = i,
                        Embedding = CreateLargeVector(i, dimensions)
                    })
                    .ToList();

                collection.Insert(documents);

                var indexOptions = new VectorIndexOptions((ushort)dimensions, VectorDistanceMetric.Euclidean);
                collection.EnsureIndex(VectorIndexName, BsonExpression.Create("$.Embedding"), indexOptions);

                (vectorPages, vectorDataPages) = CollectVectorPageUsage(db, "docs");

                vectorPages.Should().NotBeEmpty();
                vectorDataPages.Should().NotBeEmpty();

                db.Checkpoint();
            }

            Action drop = () =>
            {
                using var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename);
                db.DropCollection("docs");
                db.Checkpoint();
            };

            drop.Should().NotThrow();

            using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, file.Filename))
            {
                var vectorPageTypes = GetPageTypes(db, vectorPages);
                foreach (var kvp in vectorPageTypes)
                {
                    kvp.Value.Should().Be(PageType.Empty, $"vector index page {kvp.Key} should be reclaimed after dropping the collection");
                }

                var dataPageTypes = GetPageTypes(db, vectorDataPages);
                foreach (var kvp in dataPageTypes)
                {
                    kvp.Value.Should().Be(PageType.Empty, $"vector data page {kvp.Key} should be reclaimed after dropping the collection");
                }

                db.GetCollectionNames().Should().NotContain("docs");
            }
        }

        [Fact]
        public void DropCollection_WithVectorIndex_ReclaimsDiskSpace()
        {
            using var tempFile = new TempFile();

            using (var db = new LiteDatabase(tempFile.Filename))
            {
                var collection = db.GetCollection<VectorDocument>("vectors");
                var dimensions = 192;
                var options = new VectorIndexOptions((ushort)dimensions, VectorDistanceMetric.Cosine);

                var documents = new List<VectorDocument>();

                for (var i = 0; i < 64; i++)
                {
                    var embedding = new float[dimensions];

                    for (var j = 0; j < dimensions; j++)
                    {
                        embedding[j] = (float)Math.Cos((i + 1) * (j + 1) * 0.03125d);
                    }

                    documents.Add(new VectorDocument
                    {
                        Id = i + 1,
                        Embedding = embedding
                    });
                }

                collection.Insert(documents);
                collection.EnsureIndex(VectorIndexName, x => x.Embedding, options);

                db.Checkpoint();

                var fileInfo = new FileInfo(tempFile.Filename);
                var sizeAfterInsert = fileInfo.Length;
                sizeAfterInsert.Should().BeGreaterThan(0);

                var drop = () => db.DropCollection("vectors");

                drop.Should().NotThrow("dropping a collection that owns vector indexes should release all associated pages");

                db.Checkpoint();

                fileInfo.Refresh();
                var sizeAfterDrop = fileInfo.Length;

                sizeAfterDrop.Should().BeLessThan(sizeAfterInsert, "vector index pages must be reclaimed when the collection is dropped");
            }
        }

        private static Dictionary<PageType, int> CountPagesByType(string filename)
        {
            var counts = new Dictionary<PageType, int>();
            var buffer = new byte[Constants.PAGE_SIZE];

            using var stream = File.OpenRead(filename);

            while (stream.Read(buffer, 0, buffer.Length) == buffer.Length)
            {
                var pageType = (PageType)buffer[BasePage.P_PAGE_TYPE];
                counts.TryGetValue(pageType, out var current);
                counts[pageType] = current + 1;
            }

            return counts;
        }

        private static T ExecuteInTransaction<T>(LiteDatabase db, Func<TransactionService, T> action)
        {
            var engine = (LiteEngine)EngineField.GetValue(db);
            var method = AutoTransactionMethod.MakeGenericMethod(typeof(T));
            return (T)method.Invoke(engine, new object[] { action });
        }

        private static T InspectVectorIndex<T>(LiteDatabase db, string collection, Func<Snapshot, VectorIndexMetadata, T> selector)
        {
            return ExecuteInTransaction(db, transaction =>
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Read, collection, false);
                var metadata = snapshot.CollectionPage.GetVectorIndexMetadata(VectorIndexName);

                if (metadata == null)
                {
                    return default;
                }

                return selector(snapshot, metadata);
            });
        }

        private static (HashSet<uint> VectorPages, HashSet<uint> DataPages) CollectVectorPageUsage(LiteDatabase db, string collection)
        {
            var (vectorPages, dataPages) = InspectVectorIndex(db, collection, (snapshot, metadata) =>
            {
                var trackedVectorPages = new HashSet<uint>();
                var trackedDataPages = new HashSet<uint>();

                if (metadata.Root.IsEmpty)
                {
                    return (trackedVectorPages, trackedDataPages);
                }

                var queue = new Queue<PageAddress>();
                var visited = new HashSet<PageAddress>();
                queue.Enqueue(metadata.Root);

                while (queue.Count > 0)
                {
                    var address = queue.Dequeue();
                    if (!visited.Add(address))
                    {
                        continue;
                    }

                    var page = snapshot.GetPage<VectorIndexPage>(address.PageID);
                    var node = page.GetNode(address.Index);

                    trackedVectorPages.Add(page.PageID);

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

                    if (!node.HasInlineVector)
                    {
                        var block = node.ExternalVector;
                        while (!block.IsEmpty)
                        {
                            trackedDataPages.Add(block.PageID);

                            var dataPage = snapshot.GetPage<DataPage>(block.PageID);
                            var dataBlock = dataPage.GetBlock(block.Index);
                            block = dataBlock.NextBlock;
                        }
                    }
                }

                return (trackedVectorPages, trackedDataPages);
            });

            if (vectorPages == null || dataPages == null)
            {
                return (new HashSet<uint>(), new HashSet<uint>());
            }

            return (vectorPages, dataPages);
        }

        private static Dictionary<uint, PageType> GetPageTypes(LiteDatabase db, IEnumerable<uint> pageIds)
        {
            return ExecuteInTransaction(db, transaction =>
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Read, "$", false);
                var map = new Dictionary<uint, PageType>();

                foreach (var pageID in pageIds.Distinct())
                {
                    var page = snapshot.GetPage<BasePage>(pageID);
                    map[pageID] = page.PageType;
                }

                return map;
            });
        }

        private static float[] CreateLargeVector(int seed, int dimensions)
        {
            var vector = new float[dimensions];

            for (var i = 0; i < dimensions; i++)
            {
                vector[i] = (float)Math.Sin((seed * 0.37) + (i * 0.11));
            }

            return vector;
        }
    }
}
