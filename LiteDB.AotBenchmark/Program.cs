using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using LiteDB;
using LiteDB.Generated;

// Usage: LiteDB.AotBenchmark [documents=100000]
var documents = args.Length > 0 ? int.Parse(args[0]) : 100_000;
var path = Path.Combine(Path.GetTempPath(), $"litedb-aot-benchmark-{Guid.NewGuid():N}.db");
var results = new List<(string Name, double Milliseconds, string Check)>();

Console.WriteLine($"runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, dynamic code: {RuntimeFeature.IsDynamicCodeSupported}, documents: {documents}");

try
{
    var mapper = new BsonMapper();
    LiteDbGeneratedMappings.Register(mapper);

    using var database = new LiteDatabase(path, mapper);
    var items = database.GetCollection("items");

    Measure("insert documents", () =>
    {
        items.InsertBulk(Enumerable.Range(1, documents).Select(i => new BsonDocument
        {
            ["_id"] = i,
            ["name"] = "item-" + (i % 1000),
            ["score"] = i % 100,
            ["price"] = i * 0.5,
            ["tags"] = new BsonArray { "t" + (i % 7), "t" + (i % 11) }
        }));
        return items.Count();
    });

    Measure("create index", () => items.EnsureIndex("score", "$.score") ? 1 : 0);

    // Served by the index: the expression only runs for the index key.
    Measure("index seek (score = 42), 100 runs", () =>
    {
        var total = 0;
        for (var run = 0; run < 100; run++) total += items.Count("$.score = 42");
        return total;
    }, warm: 3);

    // Full scans: the expression runs once per document, which is where interpretation costs the most.
    Measure("full scan, simple predicate", () => items.Count("$.price > 1000"), warm: 3);
    Measure("full scan, compound predicate", () => items.Count("$.price > 1000 AND $.name LIKE 'item-1%' AND LENGTH($.name) > 6"), warm: 3);
    Measure("full scan, array predicate", () => items.Count("$.tags[*] ANY = 't3'"), warm: 3);
    Measure("projection over all documents", () => items.Query().Select("{ n: UPPER($.name), p: ROUND($.price * 1.2, 2), s: $.score + 1 }").ToEnumerable().Count(), warm: 3);
    Measure("group by with aggregates", () =>
    {
        using var reader = database.Execute("SELECT { score: @key, n: COUNT(*), total: SUM(*.price) } FROM items GROUP BY score");
        return reader.ToEnumerable().Count();
    });
    Measure("update many with expression", () => items.UpdateMany("{ price: $.price * 1.1 }", "$.score < 10"));

    // Parsing and preparing expressions that are not in the expression cache.
    Measure("create 5000 distinct expressions", () =>
    {
        var document = new BsonDocument { ["a"] = 1 };
        var total = 0;
        for (var i = 0; i < 5000; i++) total += BsonExpression.Create($"$.a + {i} > {i}").ExecuteScalar(document).AsBoolean ? 1 : 0;
        return total;
    });

    var typed = database.GetGeneratedCollection<BenchmarkRecord>("typed");
    Measure("generated collection insert", () =>
        typed.InsertBulk(Enumerable.Range(1, documents).Select(i => new BenchmarkRecord { Id = i, Name = "item-" + (i % 1000), Score = i % 100, Price = i * 0.5 })));
    Measure("generated collection read all", () => typed.FindAll().Count(), warm: 3);
    var minimum = 1000.0;
    Measure("generated collection LINQ scan", () => typed.Count(x => x.Price > minimum && x.Name.StartsWith("item-1")), warm: 3);
}
finally
{
    File.Delete(path);
    File.Delete(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-log.db"));
}

Console.WriteLine();
Console.WriteLine($"{"workload",-48} {"ms",10}  check");
foreach (var (name, milliseconds, check) in results)
{
    Console.WriteLine($"{name,-48} {milliseconds,10:F0}  {check}");
}

// Writes run once. Reads run "warm" times and report the first (cold) and the best (warm) run: the cold JIT
// figure includes JIT compilation, the warm one does not.
void Measure(string name, Func<long> workload, int warm = 0)
{
    GC.Collect();
    var clock = Stopwatch.StartNew();
    var check = workload();
    clock.Stop();
    results.Add((name + (warm > 0 ? " (cold)" : string.Empty), clock.Elapsed.TotalMilliseconds, check.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    if (warm == 0) return;

    var best = double.MaxValue;
    for (var run = 0; run < warm; run++)
    {
        clock.Restart();
        workload();
        best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
    }

    results.Add((name + " (warm)", best, check.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}

[BsonSourceGenerated]
public sealed class BenchmarkRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
    public double Price { get; set; }
}
