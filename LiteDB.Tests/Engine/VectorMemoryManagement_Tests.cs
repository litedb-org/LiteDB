using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class VectorMemoryManagement_Tests
    {
        private const int TransactionLimit = 16;
        private const int DocumentCount = 320;
        private const int Dimensions = 32;
        private const string IndexName = "embedding_idx";

        [Fact]
        public void VectorIndexBuild_PinsBounded_AndPersists()
        {
            using var file = new TempFile();
            using (var session = Open(file.Filename))
            {
                var collection = session.Database.GetCollection<VectorDocument>("vectors");
                collection.Insert(CreateDocuments());
                session.Database.Checkpoint();

                AssertWriteBound(session, () => collection.EnsureIndex(
                    IndexName,
                    x => x.Embedding,
                    new VectorIndexOptions(Dimensions, VectorDistanceMetric.Euclidean)).Should().BeTrue());
            }

            FindNearestIds(file.Filename, CreateVector(42), 5).Should().Contain(42);
        }

        [Fact]
        public void VectorSearch_PinsBounded()
        {
            using var file = CreateIndexedFile();
            using var session = Open(file.Filename);
            var collection = session.Database.GetCollection<VectorDocument>("vectors");

            AssertReadBound(session, () => collection.Query()
                .TopKNear(x => x.Embedding, CreateVector(100), 80)
                .ToArray()
                .Should().Contain(x => x.Id == 100));
        }

        [Fact]
        public void VectorSearch_Materialization_PinsBounded()
        {
            using var file = CreateIndexedFile();
            using var session = Open(file.Filename);
            var collection = session.Database.GetCollection<VectorDocument>("vectors");

            AssertReadBound(session, () => collection.Query()
                .WhereNear(x => x.Embedding, CreateVector(200), double.MaxValue)
                .ToArray()
                .Should().NotBeEmpty());
        }

        [Fact]
        public void VectorInsert_PinsBounded_AndPersists()
        {
            using var file = CreateIndexedFile();
            using (var session = Open(file.Filename))
            {
                var collection = session.Database.GetCollection<VectorDocument>("vectors");
                var inserted = new VectorDocument { Id = 10000, Embedding = CreateVector(10000), Payload = "inserted" };

                AssertWriteBound(session, () =>
                {
                    collection.Insert(inserted).AsInt32.Should().Be(10000);
                });
            }

            FindNearestIds(file.Filename, CreateVector(10000), 3).Should().Contain(10000);
        }

        [Fact]
        public void VectorUpdate_PinsBounded_AndPersists()
        {
            using var file = CreateIndexedFile();
            var updatedVector = CreateVector(10000);

            using (var session = Open(file.Filename))
            {
                var collection = session.Database.GetCollection<VectorDocument>("vectors");
                var updated = new VectorDocument { Id = 120, Embedding = updatedVector, Payload = "updated" };

                AssertWriteBound(session, () => collection.Update(updated).Should().BeTrue());
            }

            FindNearestIds(file.Filename, updatedVector, 3).Should().Contain(120);
        }

        [Fact]
        public void VectorDelete_PinsBounded_AndPersists()
        {
            using var file = CreateIndexedFile();

            using (var session = Open(file.Filename))
            {
                var collection = session.Database.GetCollection<VectorDocument>("vectors");
                AssertWriteBound(session, () => collection.Delete(120).Should().BeTrue());
            }

            using (var reopened = new LiteDatabase(file.Filename))
            {
                reopened.GetCollection<VectorDocument>("vectors").FindById(120).Should().BeNull();
            }

            FindNearestIds(file.Filename, CreateVector(120), 20).Should().NotContain(120);
        }

        [Fact]
        public void VectorIndexDrop_PinsBounded_AndPersists()
        {
            using var file = CreateIndexedFile();

            using (var session = Open(file.Filename))
            {
                var collection = session.Database.GetCollection<VectorDocument>("vectors");
                AssertWriteBound(session, () => collection.DropIndex(IndexName).Should().BeTrue());
            }

            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("$indexes")
                .Query()
                .Where("collection = 'vectors'")
                .ToDocuments()
                .Select(x => x["name"].AsString)
                .Should().NotContain(IndexName);
        }

        private static TempFile CreateIndexedFile()
        {
            var file = new TempFile();

            using (var session = Open(file.Filename))
            {
                var collection = session.Database.GetCollection<VectorDocument>("vectors");
                collection.Insert(CreateDocuments());
                collection.EnsureIndex(
                    IndexName,
                    x => x.Embedding,
                    new VectorIndexOptions(Dimensions, VectorDistanceMetric.Euclidean));
                session.Database.Checkpoint();
            }

            return file;
        }

        private static void AssertReadBound(DatabaseSession session, Action operation)
        {
            session.Database.BeginTrans().Should().BeTrue();
            var transaction = session.Engine.GetMonitor().GetThreadTransaction();

            operation();
            transaction.Safepoint();
            AssertBound(transaction);

            session.Database.Rollback().Should().BeTrue();
        }

        private static void AssertWriteBound(DatabaseSession session, Action operation)
        {
            session.Database.BeginTrans().Should().BeTrue();
            var transaction = session.Engine.GetMonitor().GetThreadTransaction();

            operation();
            transaction.Safepoint();
            AssertBound(transaction);

            session.Database.Commit().Should().BeTrue();
            session.Database.Checkpoint();
        }

        private static void AssertBound(TransactionService transaction)
        {
            transaction.MaxObservedTransactionSize.Should().BeLessThanOrEqualTo(TransactionLimit + 16);
            transaction.Pages.TransactionSize.Should().BeLessThan(TransactionLimit);
        }

        private static int[] FindNearestIds(string filename, float[] target, int limit)
        {
            using var database = new LiteDatabase(filename);
            return database.GetCollection<VectorDocument>("vectors")
                .Query()
                .TopKNear(x => x.Embedding, target, limit)
                .ToArray()
                .Select(x => x.Id)
                .ToArray();
        }

        private static IEnumerable<VectorDocument> CreateDocuments()
        {
            return Enumerable.Range(1, DocumentCount).Select(i => new VectorDocument
            {
                Id = i,
                Embedding = CreateVector(i),
                Payload = new string('x', 300)
            });
        }

        private static float[] CreateVector(int seed)
        {
            var random = new Random(seed);
            var vector = new float[Dimensions];

            for (var i = 0; i < vector.Length; i++) vector[i] = (float)(random.NextDouble() * 2 - 1);

            return vector;
        }

        private static DatabaseSession Open(string filename)
        {
            return new DatabaseSession(new EngineSettings
            {
                Filename = filename,
                CacheSize = 1024 * 1024,
                TransactionPageLimit = TransactionLimit
            });
        }

        private sealed class DatabaseSession : IDisposable
        {
            public LiteEngine Engine { get; }
            public LiteDatabase Database { get; }

            public DatabaseSession(EngineSettings settings)
            {
                this.Engine = new LiteEngine(settings);
                this.Database = new LiteDatabase(this.Engine, disposeOnClose: false);
            }

            public void Dispose()
            {
                this.Database.Dispose();
                this.Engine.Dispose();
            }
        }

        private sealed class VectorDocument
        {
            public int Id { get; set; }
            public float[] Embedding { get; set; }
            public string Payload { get; set; }
        }
    }
}
