using System.Diagnostics;
using LiteDB;
using LiteDB.Vector;

internal sealed class MemoryWorkload : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-memory-" + Guid.NewGuid().ToString("N"));
    private readonly LiteDatabase _database;
    private readonly ILiteCollection<BsonDocument> _collection;
    private readonly MeasurementReport _report;
    private readonly int _count;
    private readonly int _cacheMiB;

    public MemoryWorkload(int cacheMiB, int count, MeasurementReport report)
    {
        Directory.CreateDirectory(_directory);
        _cacheMiB = cacheMiB;
        _count = count;
        _report = report;
        _database = this.Open("data.db");
        _collection = _database.GetCollection("docs");
    }

    private LiteDatabase Open(string file, string password = null)
    {
        var connection = new ConnectionString(Path.Combine(_directory, file)) { Password = password };
        // The baseline has neither property. Do not retrofit its transaction
        // policy: the comparison must retain the old extensible budget.
        typeof(ConnectionString).GetProperty("CacheSize")?.SetValue(connection, _cacheMiB * 1024L * 1024);
        return new LiteDatabase(connection);
    }

    public void Run()
    {
        _report.Measure("seed", () => _collection.InsertBulk(Documents(0, _count)));
        _database.Checkpoint();
        _report.Measure("scanCold", () => Verify(_collection.FindAll().Count() == _count));
        _report.Measure("scanWarm", () => Verify(_collection.FindAll().Count() == _count));
        foreach (var threads in new[] { 1, 4, 16 }) this.PointLookups(threads);
        this.ReleaseLatency();

        _database.Checkpoint();
        _database.CheckpointSize = 0;
        _report.Measure("ensureIndex", () => Verify(_collection.EnsureIndex("sort_idx", "$.sort")));
        _report.Measurements["indexWalBytes"] = _database.Execute("SELECT $ FROM $database").Single()["logFileSize"].AsInt64;
        _database.Checkpoint();
        Verify(_collection.Query().OrderBy("$.sort").ToDocuments().Count() == _count);
        _report.Measurements["cacheAfterIndex"] = _database.Execute("SELECT $ FROM $database").Single()["cache"].ToString();

        // The larger leg isolates index amplification; common workloads are
        // measured on the 200k corpus for each cache profile and runtime.
        if (_count <= 200000)
        {
            _database.CheckpointSize = 1000;
            _report.Measure("bulkInsert10000", () => _collection.InsertBulk(Documents(_count, 10000)));
            this.EncryptedUpdate();
            this.VectorSearch();
            this.PressureReleaseLatency();
        }
    }

    private void PointLookups(int threads)
    {
        const int samples = 2000;
        for (var i = 0; i < 1000; i++) Verify(_collection.FindById(i % _count) != null);
        var ticks = new long[threads * samples];
        var before = this.FramesExamined();
        var missesBefore = this.CacheMetric("misses");
        using var start = new ManualResetEventSlim();
        using var ready = new CountdownEvent(threads);
        var workers = Enumerable.Range(0, threads).Select(worker => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            start.Wait();
            for (var i = 0; i < samples; i++)
            {
                var id = (worker * 7919 + i * 17) % _count;
                var begin = Stopwatch.GetTimestamp();
                var found = _collection.FindById(id);
                ticks[worker * samples + i] = Stopwatch.GetTimestamp() - begin;
                Verify(found != null);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        ready.Wait();
        var watch = Stopwatch.StartNew();
        start.Set();
        Task.WaitAll(workers);
        watch.Stop();
        _report.Latency("lookupThreads" + threads, ticks);
        _report.Measurements["lookupThreads" + threads + "TotalMs"] = watch.Elapsed.TotalMilliseconds;
        _report.Measurements["lookupThreads" + threads + "FramesExaminedPerLookup"] = (this.FramesExamined() - before) / (double)ticks.Length;
        var misses = this.CacheMetric("misses") - missesBefore;
        _report.Measurements["lookupThreads" + threads + "FramesExaminedPerMiss"] = misses == 0 ?
            (double?)null : (this.FramesExamined() - before) / (double)misses;
    }

    private long FramesExamined()
    {
        return this.CacheMetric("framesExamined");
    }

    private long CacheMetric(string name) => _database.Execute("SELECT $ FROM $database").Single()["cache"][name].AsInt64;

    private void PressureReleaseLatency()
    {
        var connection = new ConnectionString(Path.Combine(_directory, "pressure.db"));
        typeof(ConnectionString).GetProperty("CacheSize")?.SetValue(connection, 8L * 1024 * 1024);
        typeof(ConnectionString).GetProperty("TransactionPageLimit")?.SetValue(connection, 8192);
        using var database = new LiteDatabase(connection);
        var collection = database.GetCollection("docs");
        collection.InsertBulk(Documents(0, 40000));
        database.Checkpoint();
        var ticks = new long[100];
        for (var i = 0; i < ticks.Length; i++)
        {
            Verify(database.BeginTrans());
            Verify(collection.FindAll().Count() == 40000);
            var begin = Stopwatch.GetTimestamp();
            Verify(database.Rollback());
            ticks[i] = Stopwatch.GetTimestamp() - begin;
        }
        _report.Latency("overflowTransactionRelease8MiB", ticks);
        var cache = database.Execute("SELECT $ FROM $database").Single()["cache"];
        if (!cache["limitPagesRounded"].IsNull)
        {
            Verify(cache["totalPages"].AsInt32 <= cache["limitPagesRounded"].AsInt32);
            Verify(cache["pinnedPages"].AsInt32 == 0);
            Verify(cache["releasedSegments"].AsInt64 > 0);
        }
        _report.Measurements["overflowCacheAfterRelease"] = cache.ToString();
    }

    private void ReleaseLatency()
    {
        var ticks = new long[100];
        for (var i = 0; i < ticks.Length; i++)
        {
            Verify(_database.BeginTrans());
            Verify(_collection.FindAll().Take(12000).Count() == Math.Min(12000, _count));
            var begin = Stopwatch.GetTimestamp();
            Verify(_database.Rollback());
            ticks[i] = Stopwatch.GetTimestamp() - begin;
        }
        _report.Latency("transactionRelease", ticks);
    }

    private void EncryptedUpdate()
    {
        using var encrypted = this.Open("encrypted.db", "measurement-password");
        var collection = encrypted.GetCollection("docs");
        collection.InsertBulk(Documents(0, 20000));
        encrypted.Checkpoint();
        _report.Measure("encryptedUpdate20000", () => Verify(collection.UpdateMany("{ payload: 'updated' }", "_id >= 0") == 20000));
    }

    private void VectorSearch()
    {
        var vectors = _database.GetCollection<VectorDocument>("vectors");
        vectors.InsertBulk(Enumerable.Range(1, 1000).Select(i => new VectorDocument { Id = i, Embedding = Vector(i) }));
        vectors.EnsureIndex("embedding", x => x.Embedding, new VectorIndexOptions(8, VectorDistanceMetric.Euclidean));
        _database.Checkpoint();
        var minimumResults = 20;
        var shortResults = 0;
        _report.Measure("vectorSearch100", () =>
        {
            for (var i = 1; i <= 100; i++)
            {
                var results = vectors.Query().TopKNear(x => x.Embedding, Vector(i), 20).ToArray();
                Verify(results.Length > 0 && results.Length <= 20);
                Verify(results.All(x => x.Id >= 1 && x.Id <= 1000 && x.Embedding.SequenceEqual(Vector(x.Id))));
                Verify(results.Select(x => x.Id).Distinct().Count() == results.Length);
                minimumResults = Math.Min(minimumResults, results.Length);
                if (results.Length < 20) shortResults++;
            }
        });
        // The pre-feature HNSW graph can already return fewer than k results.
        // Report this independently; never silently count it as full recall.
        _report.Measurements["vectorMinimumResults"] = minimumResults;
        _report.Measurements["vectorShortResultQueries"] = shortResults;
    }

    private static float[] Vector(int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, 8).Select(_ => (float)random.NextDouble()).ToArray();
    }

    private static IEnumerable<BsonDocument> Documents(int start, int count)
    {
        return Enumerable.Range(start, count).Select(i => new BsonDocument
        {
            ["_id"] = i, ["sort"] = i, ["payload"] = new string('x', 900)
        });
    }

    private static void Verify(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Workload result verification failed");
    }

    public void Dispose()
    {
        _database.Dispose();
        // Only this fixture's GUID directory is removed, after every database closes.
        Directory.Delete(_directory, recursive: true);
    }

    public sealed class VectorDocument
    {
        public int Id { get; set; }
        public float[] Embedding { get; set; }
    }
}
