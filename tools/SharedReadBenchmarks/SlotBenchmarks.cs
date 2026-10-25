using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using LiteDB;

// Diagnostic helper measurements use delegates bound once, outside the timing window.
// They complement public-API workloads; they are not end-to-end throughput claims.
internal static class SlotBenchmarks
{
    internal static void Run(string directory, int count, int active)
    {
        if (active < 1 || active > 65536) throw new ArgumentOutOfRangeException(nameof(active));
        var type = typeof(LiteDatabase).Assembly.GetType("LiteDB.Client.Shared.SharedReaderSlots", true);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic;
        using var slots = (IDisposable)type.GetMethod("Create", flags).Invoke(null, new object[] { directory });
        var lease = (Func<int, IDisposable>)type.GetMethod("Lease", flags).CreateDelegate(typeof(Func<int, IDisposable>), slots);
        var read = (Func<string, int[]>)type.GetMethod("ReadVersions", flags).CreateDelegate(typeof(Func<string, int[]>));
        var readers = new IDisposable[active];
        var beforeFillLiveBytes = GC.GetTotalMemory(true);
        var allocated = GC.GetTotalAllocatedBytes(true);
        var filling = Stopwatch.StartNew();
        for (var i = 0; i < active; i++) readers[i] = lease(i);
        filling.Stop();
        var fillBytes = GC.GetTotalAllocatedBytes(true) - allocated;
        var liveFillBytes = GC.GetTotalMemory(true) - beforeFillLiveBytes;
        var path = Directory.GetFiles(directory, "*.lease").Single();
        try
        {
            if (!read(path).OrderBy(x => x).SequenceEqual(Enumerable.Range(0, active)))
                throw new InvalidOperationException("Incomplete live version set.");
            // Keep all preceding slots occupied: finding the one free slot scales with
            // fanout in the baseline, while actual publication and release I/O is identical.
            void Operation()
            {
                readers[active - 1].Dispose();
                readers[active - 1] = lease(active - 1);
            }
            var warmup = Stopwatch.StartNew();
            while (warmup.Elapsed.TotalSeconds < 2) Operation();
            var samples = new double[count];
            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            allocated = GC.GetTotalAllocatedBytes(true);
            for (var i = 0; i < count; i++)
            {
                var start = Stopwatch.GetTimestamp();
                Operation();
                samples[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            }
            var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
            var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
            if (!read(path).OrderBy(x => x).SequenceEqual(Enumerable.Range(0, active)))
                throw new InvalidOperationException("Churn lost a live version.");
            Array.Sort(samples);
            var close = Stopwatch.StartNew();
            foreach (var reader in readers) reader.Dispose();
            slots.Dispose();
            close.Stop();
            Array.Clear(readers, 0, readers.Length);
            var retainedAfterCloseBytes = GC.GetTotalMemory(true) - beforeFillLiveBytes;
            GC.KeepAlive(slots); // Measure the slot owner while it is still reachable.
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                scenario = "slots", active, count, fillMs = filling.Elapsed.TotalMilliseconds,
                fillBytes, liveFillBytes, retainedAfterCloseBytes, meanMs = samples.Average(), p50Ms = samples[count / 2],
                p95Ms = samples[(int)(count * .95)], p99Ms = samples[(int)(count * .99)],
                worstMs = samples[count - 1], cpuMsPerOperation = cpuMs / count,
                bytesPerOperation = bytes / (double)count, closeMs = close.Elapsed.TotalMilliseconds,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
            }));
        }
        finally { foreach (var reader in readers) reader?.Dispose(); }
    }
}
