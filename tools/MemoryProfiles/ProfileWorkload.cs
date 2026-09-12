using System.Diagnostics;
using LiteDB;

internal sealed class ProfileWorkload : IDisposable
{
    private const int DocumentCount = 100000;
    private const int HotDocuments = 4000;
    private const int SamplesPerWorker = 10000;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-profiles-" + Guid.NewGuid().ToString("N"));
    private readonly ProfileReport _report;
    private readonly LiteDatabase _database;
    private readonly ILiteCollection<BsonDocument> _collection;

    public ProfileWorkload(ProfileReport report, int cacheMiB, int? transactionPages)
    {
        _report = report;
        Directory.CreateDirectory(_directory);
        var connection = new ConnectionString(Path.Combine(_directory, "data.db"));
        var property = typeof(ConnectionString).GetProperty("MemoryProfile");
        if (property != null) property.SetValue(connection, Enum.Parse(property.PropertyType, report.Profile));
        else if (report.Profile != "Balanced") throw new ArgumentException("This library has no profiles");
        var cacheProperty = typeof(ConnectionString).GetProperty("CacheSize");
        if (cacheProperty != null) cacheProperty.SetValue(connection, cacheMiB * 1024L * 1024);
        else if (cacheMiB != 0) throw new ArgumentException("This library has no cache size setting");
        if (transactionPages.HasValue)
        {
            var transactionProperty = typeof(ConnectionString).GetProperty("TransactionPageLimit")
                ?? throw new ArgumentException("This library has no transaction page setting");
            transactionProperty.SetValue(connection, transactionPages.Value);
        }
        _database = new LiteDatabase(connection);
        _collection = _database.GetCollection("docs");
    }

    public void Run()
    {
        _report.Measure("insert", () => Verify(_collection.InsertBulk(Documents()) == DocumentCount));
        _database.Checkpoint();
        _report.Measure("firstScan", () => Verify(_collection.FindAll().Count() == DocumentCount));
        _report.Measure("warmScan", () => Verify(_collection.FindAll().Count() == DocumentCount));
        foreach (var threads in new[] { 1, 4, 16 }) this.Lookups(threads, HotDocuments, "hot");
        this.Lookups(16, DocumentCount, "scattered");
        _database.Checkpoint();
        _database.CheckpointSize = 0;
        _report.Measure("index", () => Verify(_collection.EnsureIndex("sort", "$.sort")));
        _report.Measurements["walBytes"] = this.DatabaseInfo()["logFileSize"].AsInt64;
        _database.Checkpoint();
        Verify(_collection.Query().OrderBy("$.sort").ToDocuments().Count() == DocumentCount);
        var info = this.DatabaseInfo();
        var cache = info["cache"];
        Verify(cache["writablePages"].AsInt32 == 0 && cache["pagesInUse"].AsInt32 == 0);
        // Pre-PR libraries have no bounded-cache accounting. Preserve their
        // original policy rather than retrofitting limits into the baseline.
        if (!cache["limitPagesRounded"].IsNull)
        {
            Verify(cache["lostFrames"].AsInt64 == 0 && cache["pinnedPages"].AsInt32 == 0);
            Verify(cache["loadingPages"].AsInt32 == 0);
            Verify(cache["totalPages"].AsInt32 <= cache["limitPagesRounded"].AsInt32);
        }
        _report.Measurements["cache"] = cache.ToString();
        _report.Measurements["transactionPages"] = info["transactions"]["transactionPageLimit"].IsNull ?
            null : (object)info["transactions"]["transactionPageLimit"].AsInt32;
        _report.Measurements["databaseBytes"] = new FileInfo(Path.Combine(_directory, "data.db")).Length;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _report.Measurements["retainedManagedBytes"] = GC.GetTotalMemory(false);
        _report.Measurements["workingSetBytes"] = Environment.WorkingSet;
    }

    private void Lookups(int threads, int workingSet, string name)
    {
        // The hot working set fits even in the LowMemory cache. This separates
        // synchronization costs from the eviction-heavy scattered workload.
        for (var i = 0; i < workingSet; i++) Verify(_collection.FindById(i) != null);
        var ticks = new long[threads * SamplesPerWorker];
        using var ready = new CountdownEvent(threads);
        using var start = new ManualResetEventSlim();
        var workers = Enumerable.Range(0, threads).Select(worker => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            start.Wait();
            for (var i = 0; i < SamplesPerWorker; i++)
            {
                var id = (worker * 7919 + i * 17) % workingSet;
                var begin = Stopwatch.GetTimestamp();
                var document = _collection.FindById(id);
                ticks[worker * SamplesPerWorker + i] = Stopwatch.GetTimestamp() - begin;
                Verify(document != null && document["_id"].AsInt32 == id);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        ready.Wait();
        var contentions = Monitor.LockContentionCount;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        start.Set();
        Task.WaitAll(workers);
        watch.Stop();
        contentions = Monitor.LockContentionCount - contentions;
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        Array.Sort(ticks);
        _report.Measurements[name + threads] = new
        {
            Samples = ticks.Length,
            TotalMs = watch.Elapsed.TotalMilliseconds,
            P50Us = ticks[(ticks.Length - 1) / 2] * 1e6 / Stopwatch.Frequency,
            P99Us = ticks[(int)Math.Ceiling(ticks.Length * 0.99) - 1] * 1e6 / Stopwatch.Frequency,
            OperationsPerSecond = ticks.Length / watch.Elapsed.TotalSeconds,
            Contentions = contentions,
            AllocatedBytesPerOperation = allocated / (double)ticks.Length
        };
        Console.WriteLine($"{name}{threads}: {watch.Elapsed.TotalMilliseconds:F1} ms, {contentions} contentions");
    }

    private BsonDocument DatabaseInfo() => _database.Execute("SELECT $ FROM $database").Single().AsDocument;

    private static IEnumerable<BsonDocument> Documents()
    {
        for (var i = 0; i < DocumentCount; i++)
        {
            yield return new BsonDocument { ["_id"] = i, ["sort"] = DocumentCount - i, ["payload"] = new string('x', 900) };
        }
    }

    private static void Verify(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Workload validation failed");
    }

    public void Dispose()
    {
        _database.Dispose();
        Directory.Delete(_directory, true);
    }
}
