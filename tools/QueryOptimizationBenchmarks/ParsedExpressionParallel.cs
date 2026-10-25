using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;

internal static class ParsedExpressionParallel
{
    internal static void Run(string label)
    {
        const int workers = 8;
        const int perWorker = 1024;
        const int iterations = workers * perWorker;
        ThreadPool.GetMinThreads(out var minimum, out var completion);
        ThreadPool.SetMinThreads(Math.Max(minimum, workers), completion);
        var databases = Enumerable.Range(0, workers).Select(_ => new LiteDatabase(":memory:")).ToArray();
        var results = new List<object>();
        try
        {
            var rows = databases.Select(db => db.GetCollection<Program.Row>("rows")).ToArray();
            foreach (var collection in rows)
            {
                collection.InsertBulk(Enumerable.Range(1, 20000).Select(i => new Program.Row { Id = i, Score = i }));
            }
            Measure("textir-parallel-find-by-id", (worker, i) => rows[worker].FindById(i + 1).Id);
            Measure("textir-parallel-combined", (worker, i) => rows[worker].Query()
                .Where("_id = @id AND Score >= @minimum", new BsonDocument { ["id"] = i + 1, ["minimum"] = i % 64 })
                .FirstOrDefault().Id);
            var template = BsonExpression.Create("_id = @id");
            Measure("textir-parallel-prebound-control", (worker, i) =>
                rows[worker].FindOne(template.Bind(new BsonDocument { ["id"] = i + 1 })).Id);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                label, runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                processors = Environment.ProcessorCount, assembly = typeof(LiteDatabase).Assembly.Location,
                workers, rowsPerDatabase = 20000, results
            }));
        }
        finally
        {
            foreach (var db in databases) db.Dispose();
        }

        void Measure(string name, Func<int, int, long> operation)
        {
            var checksum = RunBatch();
            var times = new double[9];
            var allocations = new double[9];
            var gen0 = new double[9];
            for (var sample = 0; sample < times.Length; sample++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var allocated = GC.GetTotalAllocatedBytes(true);
                var collections = GC.CollectionCount(0);
                var start = Stopwatch.GetTimestamp();
                checksum += RunBatch();
                times[sample] = (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / iterations;
                allocations[sample] = (GC.GetTotalAllocatedBytes(true) - allocated) / (double)iterations;
                gen0[sample] = (GC.CollectionCount(0) - collections) * 1000.0 / iterations;
            }
            results.Add(new { name, iterations, checksum, nanoseconds = times, bytes = allocations, gen0Per1000 = gen0 });
            Console.Error.WriteLine(name + ": " + times.OrderBy(x => x).ElementAt(4).ToString("F0") + " amortized ns/query");

            long RunBatch()
            {
                var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
                {
                    long sum = 0;
                    for (var i = 0; i < perWorker; i++) sum += operation(worker, i);
                    return sum;
                })).ToArray();
                Task.WaitAll(tasks);
                var sum = tasks.Sum(task => task.Result);
                if (sum != workers * (long)perWorker * (perWorker + 1) / 2)
                    throw new InvalidOperationException("Parallel queries returned unexpected fixture IDs.");
                return sum;
            }
        }
    }
}
