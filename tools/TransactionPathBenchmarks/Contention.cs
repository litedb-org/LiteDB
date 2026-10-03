using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LiteDB;

/// <summary>
/// shared-contention-{kind}-{threads}: each thread owns its connection and updates its own row.
/// shared-contention-2proc: this process measures ordinary updates while a re-launched child
/// process updates another row of the same file through its own connection.
/// </summary>
internal static class Contention
{
    public const string ChildFlag = "--child-contender";
    private const string Prefix = "shared-contention-";
    private const int Warmup = -1, After = int.MaxValue - 1, Stop = int.MaxValue;

    public static bool TryRun(RunSettings settings, JsonRecord record)
    {
        if (!settings.Workload.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        if (settings.Workload == Prefix + "2proc") RunTwoProcesses(settings, record);
        else
        {
            var parts = settings.Workload.Substring(Prefix.Length).Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var threads) || threads < 1 || threads > 32)
                throw new ArgumentException("Unknown workload " + settings.Workload);
            RunThreads(settings, record, parts[0], threads);
        }
        return true;
    }

    private sealed class Worker
    {
        public readonly LatencyHistogram Histogram = new LatencyHistogram();
        public long[] Counts;
        public Exception Error;
        public Thread Thread;
    }

    private static void RunThreads(RunSettings settings, JsonRecord record, string kind, int threads)
    {
        var before = Counters.Sample(collect: false);
        var databases = new LiteDatabase[threads];
        var workers = new Worker[threads];
        var window = Warmup;
        try
        {
            for (var i = 0; i < threads; i++)
            {
                databases[i] = new LiteDatabase(settings.Connection);
                var worker = workers[i] = new Worker { Counts = new long[settings.Windows] };
                var operation = Workloads.Contender(kind, databases[i], i + 1);
                worker.Thread = new Thread(() =>
                {
                    try
                    {
                        while (true)
                        {
                            var current = Volatile.Read(ref window);
                            if (current == Stop) return;
                            var began = Stopwatch.GetTimestamp();
                            operation();
                            var ended = Stopwatch.GetTimestamp();
                            if (current < 0 || current == After) continue;
                            worker.Histogram.Record(ended - began);
                            worker.Counts[current]++;
                        }
                    }
                    catch (Exception error) { worker.Error = error; }
                }) { IsBackground = true, Name = "contender-" + i };
            }
            foreach (var worker in workers) worker.Thread.Start();
            Thread.Sleep(settings.Warmup);
            var lengths = new double[settings.Windows];
            var gc = GcWindow.Start();
            for (var w = 0; w < settings.Windows; w++)
            {
                var start = Stopwatch.GetTimestamp();
                Volatile.Write(ref window, w);
                Thread.Sleep(settings.Window);
                lengths[w] = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
            }
            Volatile.Write(ref window, After);
            var allocation = gc.Stop();
            // Sampled while every contender is still running, after the allocation total is taken.
            var peak = Counters.Sample(collect: false);
            Volatile.Write(ref window, Stop);
            foreach (var worker in workers)
            {
                if (!worker.Thread.Join(TimeSpan.FromMinutes(2))) throw new TimeoutException("A contender did not stop");
                if (worker.Error != null) throw new InvalidOperationException("Contender failed", worker.Error);
            }
            // Read histograms only after their writers stopped; at most one in-flight operation
            // per contender separates the allocation total from the counted operations.
            var histogram = new LatencyHistogram();
            foreach (var worker in workers) histogram.Merge(worker.Histogram);
            allocation.WriteTo(record, histogram.Count);
            var rates = new double[settings.Windows];
            long minimum = long.MaxValue, maximum = 0;
            foreach (var worker in workers)
            {
                long total = 0;
                for (var w = 0; w < rates.Length; w++)
                {
                    rates[w] += worker.Counts[w] / lengths[w];
                    total += worker.Counts[w];
                }
                minimum = Math.Min(minimum, total);
                maximum = Math.Max(maximum, total);
            }
            record.Add("kind", "contention").Add("opsPerSecond", Bench.Median(rates)).Add("windows", rates);
            histogram.WriteTo(record);
            record.Add("workers", threads).Add("workerMinOps", minimum).Add("workerMaxOps", maximum)
                .Add("threadsBefore", before.Threads).Add("peakThreads", peak.Threads)
                .Add("handlesBefore", before.Handles).Add("peakHandles", peak.Handles);
        }
        finally
        {
            Volatile.Write(ref window, Stop);
            foreach (var worker in workers) worker?.Thread?.Join(TimeSpan.FromMinutes(2));
            foreach (var database in databases) database?.Dispose();
        }
    }

    private static void RunTwoProcesses(RunSettings settings, JsonRecord record)
    {
        using var db = new LiteDatabase(settings.Connection);
        var operation = Workloads.Contender("ordinary", db, 1);
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown host path");
        var entry = typeof(Contention).Assembly.Location;
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true };
        // Under `dotnet runner.dll` the host is the muxer and needs the entry assembly first.
        if (!string.Equals(Path.GetFileNameWithoutExtension(host), Path.GetFileNameWithoutExtension(entry), StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(entry);
        start.ArgumentList.Add(ChildFlag);
        start.ArgumentList.Add(settings.File);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Child contender did not start");
        try
        {
            if (child.StandardOutput.ReadLine() != "ready") throw new InvalidOperationException("Child contender did not start contending");
            var before = Counters.Sample(collect: false);
            Latency.Measure(operation, settings, record, "contention");
            child.StandardInput.WriteLine("stop");
            child.StandardInput.Close();
            var childResult = child.StandardOutput.ReadLine();
            if (!child.WaitForExit(120_000) || child.ExitCode != 0 || childResult == null)
                throw new InvalidOperationException("Child contender failed");
            record.Add("workers", 2L).Raw("child", childResult)
                .Add("threadsBefore", before.Threads).Add("handlesBefore", before.Handles);
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }

    /// <summary>The re-launched contender: ordinary updates of row 2 until stdin says stop or closes.</summary>
    public static int RunChild(string file)
    {
        using var db = new LiteDatabase(Bench.Connect(file, shared: true));
        var operation = Workloads.Contender("ordinary", db, 2);
        var stop = 0;
        new Thread(() =>
        {
            Console.In.ReadLine();
            Volatile.Write(ref stop, 1);
        }) { IsBackground = true }.Start();
        operation();
        Console.Out.WriteLine("ready");
        Console.Out.Flush();
        long operations = 0;
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref stop) == 0)
        {
            operation();
            operations++;
        }
        Console.Out.WriteLine(new JsonRecord().Add("operations", operations)
            .Add("opsPerSecond", operations / clock.Elapsed.TotalSeconds).ToString());
        return 0;
    }
}
