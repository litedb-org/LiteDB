using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using LiteDB.Vector;

namespace LiteDB.Benchmarks.Benchmarks
{
    /// <summary>
    /// Exercises the page-cache and safepoint hot paths at the supported cache
    /// profiles. The same fixture covers plain/encrypted writes, concurrent
    /// readers, vector traversal, transaction-end trim, and index building.
    /// </summary>
    [MemoryDiagnoser]
    public class MemoryManagementBenchmarks
    {
        private const int DocumentCount = 20000;
        private string _filename;
        private LiteDatabase _database;
        private ILiteCollection<BenchmarkDocument> _collection;
        private int _nextID;

        [Params(8, 64, 256)]
        public int CacheSizeMB { get; set; }

        [Params(null, "benchmark-password")]
        public string Password { get; set; }

        [Params(1, 4, 16)]
        public int ReaderThreads { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _filename = Path.Combine(Path.GetTempPath(), "litedb-memory-benchmark-" + Guid.NewGuid() + ".db");
            _database = new LiteDatabase(new ConnectionString(_filename)
            {
                CacheSize = CacheSizeMB * 1024L * 1024L,
                Password = Password,
                TransactionPageLimit = 1000
            });
            _collection = _database.GetCollection<BenchmarkDocument>("docs");
            _collection.InsertBulk(Enumerable.Range(1, DocumentCount).Select(CreateDocument));
            _collection.EnsureIndex("age_idx", x => x.Age);
            _collection.EnsureIndex(
                "embedding_idx",
                x => x.Embedding,
                new VectorIndexOptions(8, VectorDistanceMetric.Euclidean));
            _database.Checkpoint();
            _nextID = DocumentCount;
        }

        [Benchmark(Baseline = true)]
        public BenchmarkDocument PointLookup()
        {
            return _collection.FindById((Environment.TickCount & int.MaxValue) % DocumentCount + 1);
        }

        [Benchmark]
        public int FullScan()
        {
            return _collection.FindAll().Count();
        }

        [Benchmark]
        public int BulkWrite()
        {
            var start = Interlocked.Add(ref _nextID, 100) - 99;
            return _collection.InsertBulk(Enumerable.Range(start, 100).Select(CreateDocument));
        }

        [Benchmark]
        public int BulkUpdate()
        {
            return _collection.UpdateMany("{ payload: 'updated' }", "age = 5");
        }

        [Benchmark]
        public int VectorSearch()
        {
            return _collection.Query()
                .TopKNear(x => x.Embedding, CreateVector(42), 20)
                .ToArray()
                .Length;
        }

        [Benchmark]
        public int ConcurrentPointLookups()
        {
            var found = 0;

            Parallel.For(0, ReaderThreads, worker =>
            {
                for (var i = 0; i < 250; i++)
                {
                    if (_collection.FindById((worker * 251 + i) % DocumentCount + 1) != null)
                    {
                        Interlocked.Increment(ref found);
                    }
                }
            });

            return found;
        }

        [Benchmark]
        public int TransactionEndTrim()
        {
            _database.BeginTrans();
            var count = _collection.FindAll().Take(5000).Count();
            _database.Rollback();
            return count;
        }

        [Benchmark]
        public bool DropAndEnsureIndex()
        {
            _collection.DropIndex("build_idx");
            return _collection.EnsureIndex("build_idx", x => x.Payload);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _database?.Dispose();
            DeleteIfExists(_filename);
            DeleteIfExists(Path.ChangeExtension(_filename, "-log.db"));
            DeleteIfExists(Path.ChangeExtension(_filename, "-tmp.db"));
        }

        private static BenchmarkDocument CreateDocument(int id)
        {
            return new BenchmarkDocument
            {
                Id = id,
                Age = id % 90,
                Payload = new string('x', 900),
                Embedding = CreateVector(id)
            };
        }

        private static float[] CreateVector(int seed)
        {
            var random = new Random(seed);
            return Enumerable.Range(0, 8)
                .Select(_ => (float)(random.NextDouble() * 2 - 1))
                .ToArray();
        }

        private static void DeleteIfExists(string filename)
        {
            if (!string.IsNullOrEmpty(filename) && File.Exists(filename)) File.Delete(filename);
        }

        public sealed class BenchmarkDocument
        {
            public int Id { get; set; }
            public int Age { get; set; }
            public string Payload { get; set; }
            public float[] Embedding { get; set; }
        }
    }
}
