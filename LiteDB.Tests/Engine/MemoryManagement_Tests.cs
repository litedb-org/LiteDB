using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Engine
{
    public class MemoryManagement_Tests
    {
        private const int TransactionLimit = 16;

        [Fact]
        public void Scan_PinsAtMostThresholdPlusOneDocument()
        {
            using var harness = CreateHarness();
            var collection = Seed(harness.Database);

            AssertOperationBound(harness, () => collection.FindAll().Count().Should().Be(1200));
        }

        [Fact]
        public void OpenCursor_PinsAtMostThresholdPlusOneDocument()
        {
            using var harness = CreateHarness();
            var collection = Seed(harness.Database);
            using var cursor = collection.FindAll().GetEnumerator();

            for (var i = 0; i < 600; i++) cursor.MoveNext().Should().BeTrue();

            var transaction = harness.Engine.GetMonitor().GetThreadTransaction();
            transaction.Should().NotBeNull();
            transaction.Pages.TransactionSize.Should().BeLessThanOrEqualTo(TransactionLimit + 4);
        }

        [Fact]
        public void OrderBy_NoIndex_PinsBounded()
        {
            using var harness = CreateHarness();
            var collection = Seed(harness.Database);

            AssertOperationBound(harness, () => collection.Query()
                .OrderBy("$.sort")
                .ToDocuments()
                .Count()
                .Should().Be(1200));
        }

        [Fact]
        public void Include_Array_PinsBounded()
        {
            using var harness = CreateHarness();
            var targets = Seed(harness.Database, "targets");
            var references = new BsonArray(Enumerable.Range(0, 1200).Select(i => new BsonDocument
            {
                ["$id"] = i,
                ["$ref"] = "targets"
            }));
            var parents = harness.Database.GetCollection("parents");
            parents.Insert(new BsonDocument { ["_id"] = 1, ["references"] = references });
            harness.Database.Checkpoint();

            AssertOperationBound(harness, () => parents.Query()
                .Include("$.references")
                .ToDocuments()
                .Single()["references"].AsArray.Count
                .Should().Be(1200));

            targets.Count().Should().Be(1200);
        }

        [Fact]
        public void Like_NoMatch_PinsBounded()
        {
            using var harness = CreateHarness();
            var collection = Seed(harness.Database);
            collection.EnsureIndex("name_idx", "$.name");
            harness.Database.Checkpoint();

            AssertOperationBound(harness, () => collection.Find(Query.Contains("name", "not-present"))
                .Should().BeEmpty());
        }

        [Fact]
        public void Like_WithPrefixAndNoMatch_PinsBounded()
        {
            using var harness = CreateHarness();
            var collection = Seed(harness.Database);
            collection.EnsureIndex("name_idx", "$.name");
            harness.Database.Checkpoint();

            AssertOperationBound(harness, () => collection
                .Find(BsonExpression.Create("$.name LIKE 'name-%-not-present'"))
                .Should().BeEmpty());
        }

        [Fact]
        public void Like_WithPrefixAndMatches_PinsBounded()
        {
            using var harness = CreateHarness();
            var collection = Seed(harness.Database);
            collection.EnsureIndex("name_idx", "$.name");
            harness.Database.Checkpoint();

            AssertOperationBound(harness, () => collection
                .Find(BsonExpression.Create("$.name LIKE 'name-1%'"))
                .Should().HaveCount(311));
        }

        [Fact]
        public void NotEqual_NoMatch_PinsBounded()
        {
            using var harness = CreateHarness();
            var collection = Seed(harness.Database, valueFactory: _ => 1);
            collection.EnsureIndex("sort_idx", "$.sort");
            harness.Database.Checkpoint();

            AssertOperationBound(harness, () => collection.Find(Query.Not("sort", 1))
                .Should().BeEmpty());
        }

        [Fact]
        public void IndexIn_AbsentValues_PinsBounded()
        {
            using var harness = CreateHarness();
            var collection = Seed(harness.Database);
            collection.EnsureIndex("sort_idx", "$.sort");
            harness.Database.Checkpoint();
            var missing = Enumerable.Range(10000, 2000).Select(x => (BsonValue)x);

            AssertOperationBound(harness, () => collection.Find(Query.In("sort", missing))
                .Should().BeEmpty());
        }

        [Fact]
        public void Aggregate_Replay_PinsBounded()
        {
            using var harness = CreateHarness();
            var collection = Seed(harness.Database);

            AssertOperationBound(harness, () => collection.Query()
                .GroupBy("$.group")
                .Select("{ key: @key, count: COUNT(*) }")
                .ToDocuments()
                .Should().HaveCount(10));
        }

        [Fact]
        public void Aggregate_CursorDisposedEarly_ReleasesSourcePages()
        {
            using var harness = CreateHarness();
            var collection = Seed(harness.Database);
            using (var cursor = collection.Query()
                .Select("{ count: COUNT(*) }")
                .ToDocuments()
                .GetEnumerator())
            {
                cursor.MoveNext().Should().BeTrue();
            }

            GetCacheInfo(harness.Database)["pinnedPages"].AsInt32.Should().Be(0);
        }

        [Fact]
        public void Delete_AbsentIds_PinsBounded()
        {
            using var harness = CreateHarness();
            Seed(harness.Database);
            var missing = Enumerable.Range(10000, 2000).Select(x => (BsonValue)x);

            AssertOperationBound(harness, () => harness.Engine.Delete("docs", missing).Should().Be(0), commit: true);
        }

        [Fact]
        public void DropIndex_PinsBounded_AndPersists()
        {
            using var file = new TempFile();

            using (var engine = new LiteEngine(new EngineSettings
            {
                Filename = file.Filename,
                CacheSize = 1024 * 1024,
                TransactionPageLimit = TransactionLimit
            }))
            using (var database = new LiteDatabase(engine, disposeOnClose: false))
            {
                var collection = Seed(database);
                collection.EnsureIndex("sort_idx", "$.sort");
                database.Checkpoint();

                database.BeginTrans().Should().BeTrue();
                var transaction = engine.GetMonitor().GetThreadTransaction();
                collection.DropIndex("sort_idx").Should().BeTrue();
                transaction.Safepoint();
                transaction.MaxObservedTransactionSize.Should().BeLessThanOrEqualTo(TransactionLimit + 16);
                database.Commit().Should().BeTrue();
                database.Checkpoint();
            }

            using (var reopened = new LiteDatabase(file.Filename))
            {
                reopened.GetCollection("$indexes")
                    .Query()
                    .Where("collection = 'docs'")
                    .ToDocuments()
                    .Select(x => x["name"].AsString)
                    .Should().NotContain("sort_idx");
            }
        }

        [Fact]
        public void Scan_DoesNotGrowCacheBeyondLimit()
        {
            using var file = new TempFile();
            using var database = new LiteDatabase(new ConnectionString(file.Filename)
            {
                CacheSize = 512 * 1024,
                TransactionPageLimit = 32
            });
            var collection = database.GetCollection("docs");
            collection.InsertBulk(CreateDocuments(5000));
            database.Checkpoint();

            collection.FindAll().Count().Should().Be(5000);
            collection.FindAll().Count().Should().Be(5000);

            var cache = GetCacheInfo(database);
            cache["allocatedBytes"].AsInt64.Should().BeLessThanOrEqualTo(cache["limitPagesRounded"].AsInt32 * (long)PAGE_SIZE);
            cache["pinnedPages"].AsInt32.Should().Be(0);
            cache["lostFrames"].AsInt64.Should().Be(0);
        }

        [Fact]
        public void BulkWrite_ReusesFrames()
        {
            using var harness = CreateHarness(cacheSize: 512 * 1024, transactionLimit: 32);
            var collection = harness.Database.GetCollection("docs");

            for (var batch = 0; batch < 12; batch++)
            {
                collection.InsertBulk(CreateDocuments(400, batch * 400));
                var cache = GetCacheInfo(harness.Database);
                cache["allocatedBytes"].AsInt64.Should().BeLessThanOrEqualTo(cache["limitPagesRounded"].AsInt32 * (long)PAGE_SIZE);
                cache["pinnedPages"].AsInt32.Should().Be(0);
            }

            collection.Count().Should().Be(4800);
        }

        [Fact]
        public void DisposedDatabaseAndEngine_AreCollectable()
        {
            var references = CreateDisposedDatabaseReferences();

            ForceFullCollection();

            references.Should().OnlyContain(reference => !reference.IsAlive);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void MemoryDb_LogCapacityReleasedOnCheckpoint(string password)
        {
            using var engine = new LiteEngine(new EngineSettings
            {
                Filename = ":memory:",
                Password = password,
                TransactionPageLimit = 32
            });
            using var database = new LiteDatabase(engine, disposeOnClose: false);
            database.CheckpointSize = 0;
            var collection = database.GetCollection("docs");
            collection.InsertBulk(CreateDocuments(3000));
            var log = GetOwnedLogStream(engine);
            var beforeCapacity = log.Capacity;

            beforeCapacity.Should().BeGreaterThan(password == null ? 0 : PAGE_SIZE);
            database.Checkpoint();

            log.Length.Should().Be(password == null ? 0 : PAGE_SIZE);
            log.Capacity.Should().Be((int)log.Length);
            log.Capacity.Should().BeLessThan(beforeCapacity);
        }

        [Fact]
        public void TransactionMonitor_DisposesThreadLocalSlot()
        {
            var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            var monitor = engine.GetMonitor();

            engine.Dispose();

            Action accessDisposedSlot = () => monitor.GetThreadTransaction();
            accessDisposedSlot.Should().Throw<ObjectDisposedException>();
        }

        [Fact]
        public async Task TransactionMonitor_DisposeRacingCreation_RejectsNewTransaction()
        {
            var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            var monitor = engine.GetMonitor();
            using var creationStarting = new ManualResetEventSlim();
            using var resumeCreation = new ManualResetEventSlim();
            monitor.BeforeTransactionCreationLock = () =>
            {
                creationStarting.Set();
                resumeCreation.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            };

            var creation = Task.Run(() => Record.Exception(() =>
                monitor.GetTransaction(true, true, out _)));
            creationStarting.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

            engine.Dispose();
            resumeCreation.Set();

            (await creation).Should().BeOfType<ObjectDisposedException>();
            monitor.GetTransactionsSnapshot().Should().BeEmpty();
        }

        [Fact]
        public void DatabaseReportsMemoryAccounting()
        {
            using var harness = CreateHarness();
            Seed(harness.Database);
            var info = harness.Database.Execute("SELECT $ FROM $database").First().AsDocument;
            var cache = info["cache"].AsDocument;
            var transactions = info["transactions"].AsDocument;
            var expectedCacheFields = new[]
            {
                "limitBytes", "limitPagesRounded", "allocatedBytes", "segments", "totalPages",
                "freePages", "readablePages", "idleReadablePages", "writablePages", "loadingPages",
                "pinnedPages", "retainedBySegments", "evictedPages", "releasedSegments",
                "overflowSegments", "framesExamined", "budgetExceeded", "lostFrames", "hits",
                "misses", "compiledExpressions"
            };

            cache.Keys.Should().Contain(expectedCacheFields);
            cache["totalPages"].AsInt32.Should().Be(
                cache["freePages"].AsInt32 +
                cache["readablePages"].AsInt32 +
                cache["writablePages"].AsInt32 +
                cache["loadingPages"].AsInt32);
            cache["readablePages"].AsInt32.Should().Be(
                cache["idleReadablePages"].AsInt32 + cache["pinnedPages"].AsInt32);
            transactions.Keys.Should().Contain(new[] { "transactionPageLimit", "transactionPages" });
            transactions.Keys.Should().NotContain("pinnedPages");
            transactions["open"].AsInt32.Should().Be(transactions["transactionPages"].AsArray.Count);
            transactions["transactionPages"].AsArray.All(x =>
                x.AsDocument.Keys.Contains("transactionID") &&
                x.AsDocument.Keys.Contains("pages")).Should().BeTrue();
            transactions.Keys.Should().NotContain(new[] { "availableSize", "initialTransactionSize" });
        }

        private static void AssertOperationBound(DatabaseHarness harness, Action operation, bool commit = false)
        {
            harness.Database.BeginTrans().Should().BeTrue();
            var transaction = harness.Engine.GetMonitor().GetThreadTransaction();

            operation();
            transaction.Safepoint();

            transaction.MaxObservedTransactionSize.Should().BeLessThanOrEqualTo(TransactionLimit + 16);
            transaction.Pages.TransactionSize.Should().BeLessThan(TransactionLimit);

            if (commit)
            {
                harness.Database.Commit().Should().BeTrue();
            }
            else
            {
                harness.Database.Rollback().Should().BeTrue();
            }

            GetCacheInfo(harness.Database)["pinnedPages"].AsInt32.Should().Be(0);
        }

        private static ILiteCollection<BsonDocument> Seed(
            LiteDatabase database,
            string name = "docs",
            Func<int, int> valueFactory = null)
        {
            var collection = database.GetCollection(name);
            collection.InsertBulk(CreateDocuments(1200, valueFactory: valueFactory));
            database.Checkpoint();
            return collection;
        }

        private static IEnumerable<BsonDocument> CreateDocuments(
            int count,
            int start = 0,
            Func<int, int> valueFactory = null)
        {
            return Enumerable.Range(start, count).Select(i => new BsonDocument
            {
                ["_id"] = i,
                ["name"] = "name-" + i,
                ["sort"] = valueFactory?.Invoke(i) ?? i,
                ["group"] = i % 10,
                ["payload"] = new string('x', 900)
            });
        }

        private static BsonDocument GetCacheInfo(LiteDatabase database)
        {
            return database.Execute("SELECT $ FROM $database").First().AsDocument["cache"].AsDocument;
        }

        private static MemoryStream GetOwnedLogStream(LiteEngine engine)
        {
            var diskField = typeof(LiteEngine).GetField("_disk", BindingFlags.Instance | BindingFlags.NonPublic);
            var disk = diskField.GetValue(engine);
            var factoryField = typeof(DiskService).GetField("_logFactory", BindingFlags.Instance | BindingFlags.NonPublic);
            var factory = factoryField.GetValue(disk);
            var streamField = typeof(StreamFactory).GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic);

            return (MemoryStream)streamField.GetValue(factory);
        }

        private static DatabaseHarness CreateHarness(long cacheSize = 1024 * 1024, int transactionLimit = TransactionLimit)
        {
            return new DatabaseHarness(new EngineSettings
            {
                DataStream = new MemoryStream(),
                LogStream = new MemoryStream(),
                TempStream = new MemoryStream(),
                CacheSize = cacheSize,
                TransactionPageLimit = transactionLimit
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ICollection<WeakReference> CreateDisposedDatabaseReferences()
        {
            var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            var database = new LiteDatabase(engine, disposeOnClose: false);
            database.GetCollection("docs").InsertBulk(CreateDocuments(100));
            var references = new[] { new WeakReference(database), new WeakReference(engine) };

            database.Dispose();
            engine.Dispose();

            return references;
        }

        private static void ForceFullCollection()
        {
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        private sealed class DatabaseHarness : IDisposable
        {
            public LiteEngine Engine { get; }
            public LiteDatabase Database { get; }

            public DatabaseHarness(EngineSettings settings)
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
    }
}
