using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using LiteDB;

internal static class Program
{
    private static void Main(string[] args)
    {
        using var db = new LiteDatabase(":memory:");
        var rows = db.GetCollection("rows");
        rows.Insert(new BsonDocument { ["_id"] = 1, ["Values"] = new BsonArray(1, 2, 3) });
        rows.Query().Select("{ values: ARRAY(MAP(Values => @ + 1)) }").ToList();
        var baseline = GC.GetTotalMemory(true);
        var start = Stopwatch.GetTimestamp();
        var queries = ExecuteQueries(rows);
        var elapsed = (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency;
        var retained = GC.GetTotalMemory(true) - baseline;
        var retainedArrays = queries.OriginalArrays.Count(x => x.IsAlive);
        if (queries.Checksum != 256) throw new InvalidOperationException("Unexpected query results: " + queries.Checksum);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            label = args.FirstOrDefault(), runtime = RuntimeInformation.FrameworkDescription,
            assembly = typeof(LiteDatabase).Assembly.Location, rows = 1, templates = 64,
            originalKeysPerTemplate = 10000, reboundKeysPerTemplate = 1,
            completeQueries = 128, checksum = queries.Checksum, retainedOriginalArrays = retainedArrays,
            liveHeapGrowthBytes = retained, nanosecondsPerQuery = elapsed / 128
        }, new JsonSerializerOptions { WriteIndented = true }));
        GC.KeepAlive(queries.Templates);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static QueryState ExecuteQueries(ILiteCollection<BsonDocument> rows)
    {
        var state = new QueryState();
        for (var i = 0; i < 64; i++)
        {
            var name = "keys" + i;
            var keys = new BsonArray(Enumerable.Range(0, 10000).Select(x => new BsonValue(x)));
            var expression = BsonExpression.Create("{ values: ARRAY(MAP(Values => @ IN @" + name + ")) }",
                new BsonDocument { [name] = keys });
            state.Checksum += Read(rows, expression);
            var rebound = expression.Bind(new BsonDocument { [name] = new BsonArray(2) });
            state.Checksum += Read(rows, rebound);
            state.Templates.Add(rebound);
            state.OriginalArrays.Add(new WeakReference(keys));
        }
        return state;
    }

    private static int Read(ILiteCollection<BsonDocument> rows, BsonExpression projection) =>
        rows.Query().Select(projection).ToList().Sum(row => row["values"].AsArray.Count(value => value.AsBoolean));

    private class QueryState
    {
        internal readonly List<BsonExpression> Templates = new List<BsonExpression>();
        internal readonly List<WeakReference> OriginalArrays = new List<WeakReference>();
        internal int Checksum;
    }
}
