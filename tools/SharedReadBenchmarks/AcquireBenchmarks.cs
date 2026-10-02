using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using LiteDB;

/// <summary>
/// Contended writer acquisition across processes. Every worker runs a fixed number of
/// explicit transactions on one Shared database and stamps each with
/// <see cref="Stopwatch.GetTimestamp"/>: arrival immediately before
/// <see cref="LiteDatabase.BeginTrans"/>, acquisition immediately after it returns, and
/// release immediately after <see cref="LiteDatabase.Commit"/> returns.
/// </summary>
/// <remarks>
/// In Shared mode BeginTrans returns only after this process owns the named writer mutex,
/// has opened the engine and started the transaction; ownership lasts until Commit. The
/// acquisition stamp is therefore taken inside the exclusive ownership interval: it is
/// late by the engine open and transaction start (a per-build constant plus noise), but
/// the order of acquisition stamps across processes is the true ownership order. The
/// ticket document makes that checkable: each transaction increments it, so ticket order
/// must equal acquisition-stamp order. The arrival stamp precedes the native mutex request
/// by the call frame only (microseconds). GetTimestamp reads CLOCK_MONOTONIC on Linux and
/// QueryPerformanceCounter on Windows, both system-wide, so stamps of different processes
/// on one host are comparable; the driver brackets them with its own reads of that clock.
/// </remarks>
internal static class AcquireBenchmarks
{
    private const string Collection = "acquire";
    private static readonly string Payload = new string('a', 64);

    // acquire <database> <worker> <warmup> <iterations> <think-us> <signal> <samples-file>
    internal static void Run(string[] args)
    {
        var filename = Path.GetFullPath(args[1]);
        var worker = int.Parse(args[2], CultureInfo.InvariantCulture);
        var warmup = int.Parse(args[3], CultureInfo.InvariantCulture);
        var iterations = int.Parse(args[4], CultureInfo.InvariantCulture);
        var thinkUs = int.Parse(args[5], CultureInfo.InvariantCulture);
        var signal = Path.GetFullPath(args[6]);
        var output = Path.GetFullPath(args[7]);
        if (worker < 0 || warmup < 0 || iterations <= 0 || thinkUs < 0)
            throw new ArgumentOutOfRangeException(nameof(args), "Usage: acquire <database> <worker> <warmup> <iterations> <think-us> <signal> <samples-file>");
        var thinkTicks = thinkUs * Stopwatch.Frequency / 1_000_000;
        var samples = new long[iterations][];

        using (var db = new LiteDatabase(new ConnectionString { Filename = filename, Connection = ConnectionType.Shared }))
        {
            var rows = db.GetCollection(Collection);
            // Warm up JIT and the first engine open outside the measured interval.
            for (var i = 0; i < warmup; i++)
            {
                Begin(db);
                Write(db, rows, worker, i + 1);
                Think(thinkTicks);
            }
            WaitForStart(signal, worker);
            for (var i = 0; i < iterations; i++)
            {
                var arrival = Stopwatch.GetTimestamp();
                Begin(db);
                var acquired = Stopwatch.GetTimestamp();
                var ticket = Write(db, rows, worker, warmup + i + 1);
                var released = Stopwatch.GetTimestamp();
                samples[i] = new[] { i, ticket, arrival, acquired, released };
                Think(thinkTicks);
            }
        }

        var record = new
        {
            worker, warmup, iterations, thinkUs, frequency = Stopwatch.Frequency,
            isHighResolution = Stopwatch.IsHighResolution,
            fields = new[] { "seq", "ticket", "arrival", "acquired", "released" }, samples
        };
        var temporary = output + ".partial";
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(record));
        File.Move(temporary, output);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { worker, count = samples.Length, frequency = Stopwatch.Frequency }));
    }

    // acquire-verify <database> <workers> <transactions-per-worker>
    internal static void Verify(string filename, int workers, int perWorker)
    {
        using var db = new LiteDatabase(new ConnectionString { Filename = Path.GetFullPath(filename), Connection = ConnectionType.Shared });
        var rows = db.GetCollection(Collection);
        var ticket = rows.FindById("ticket");
        if (ticket == null || ticket["value"].AsInt64 != (long)workers * perWorker)
            throw new InvalidOperationException("Ticket count does not match the acknowledged transactions");
        for (var worker = 0; worker < workers; worker++)
        {
            var row = rows.FindById("worker" + worker);
            if (row == null || row["revision"].AsInt32 != perWorker || row["payload"].AsString != Payload)
                throw new InvalidOperationException("Lost acknowledged state for worker " + worker);
        }
        if (rows.Count() != workers + 1) throw new InvalidOperationException("Unexpected documents");
        Console.WriteLine("verified");
    }

    private static void Begin(LiteDatabase db)
    {
        if (!db.BeginTrans()) throw new InvalidOperationException("BeginTrans joined an existing transaction");
    }

    private static long Write(LiteDatabase db, ILiteCollection<BsonDocument> rows, int worker, int revision)
    {
        // Read-modify-write under the writer: the ticket is the global acquisition sequence.
        var ticket = (rows.FindById("ticket")?["value"].AsInt64 ?? 0) + 1;
        rows.Upsert(new BsonDocument { ["_id"] = "ticket", ["value"] = ticket });
        rows.Upsert(new BsonDocument { ["_id"] = "worker" + worker, ["revision"] = revision, ["payload"] = Payload });
        if (!db.Commit()) throw new InvalidOperationException("Transaction did not commit");
        return ticket;
    }

    private static void Think(long ticks)
    {
        if (ticks <= 0) return;
        var until = Stopwatch.GetTimestamp() + ticks;
        // polling: fixed benchmark think time; a sleep would quantize it to the OS timer.
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(16);
    }

    private static void WaitForStart(string signal, int worker)
    {
        File.WriteAllText(signal + ".ready-" + worker, "ready");
        var wait = Stopwatch.StartNew();
        // polling: start barrier on a file published by the driver, outside the measured loop.
        while (!File.Exists(signal))
        {
            if (wait.Elapsed.TotalSeconds > 60) throw new TimeoutException("Start barrier");
            Thread.Sleep(1);
        }
        var start = new DateTime(long.Parse(File.ReadAllText(signal), CultureInfo.InvariantCulture), DateTimeKind.Utc);
        // polling: synchronized start; every worker leaves this loop within the timer resolution.
        while (DateTime.UtcNow < start) Thread.Sleep(1);
    }
}
