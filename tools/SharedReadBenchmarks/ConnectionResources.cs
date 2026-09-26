using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using LiteDB;

internal static class ConnectionResources
{
    internal static void Run(string scratch)
    {
        var directory = Path.Combine(Path.GetFullPath(scratch), "connections-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "test.db");
        var connections = new LiteDatabase[64];
        var payload = new string('p', 1000);
        var passed = false;
        using var process = Process.GetCurrentProcess();
        object Sample()
        {
            process.Refresh();
            long? proportionalBytes = null;
            if (File.Exists("/proc/self/smaps_rollup"))
            {
                var line = File.ReadLines("/proc/self/smaps_rollup").First(x => x.StartsWith("Pss:", StringComparison.Ordinal));
                proportionalBytes = long.Parse(line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
            }
            return new { managedBytes = GC.GetTotalMemory(false), workingSetBytes = process.WorkingSet64,
                peakWorkingSetBytes = process.PeakWorkingSet64, proportionalBytes,
                threads = process.Threads.Count, handles = process.HandleCount,
                files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length };
        }
        void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        void Read(LiteDatabase db)
        {
            var row = db.GetCollection("rows").FindById(1);
            if (row == null || row.Count != 2 || row["_id"].AsInt32 != 1 || row["payload"].AsString != payload)
                throw new InvalidOperationException("Full payload mismatch");
        }
        try
        {
            using (var seed = new LiteDatabase(file))
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = payload });
            Collect();
            var before = Sample();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var cpu = process.TotalProcessorTime;
            var warmup = Stopwatch.StartNew();
            for (var i = 0; i < connections.Length; i++)
                connections[i] = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared });
            var reads = 0;
            do
            {
                foreach (var db in connections) { Read(db); reads++; }
            } while (warmup.Elapsed.TotalSeconds < 3);
            var warmupMs = warmup.Elapsed.TotalMilliseconds;
            var warm = Sample();
            Thread.Sleep(1500);
            Collect();
            var idleAlive = Sample();
            var close = Stopwatch.StartNew();
            foreach (var db in connections) db.Dispose();
            close.Stop();
            Thread.Sleep(1500);
            Collect();
            var closed = Sample();
            var lifecycleCpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var lifecycleAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
            GC.KeepAlive(connections);
            using (var cold = new LiteDatabase(file)) Read(cold);
            var binary = typeof(LiteDatabase).Assembly.Location;
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                count = connections.Length, reads, warmupMs, before, warm, idleAlive, closed,
                closeMs = close.Elapsed.TotalMilliseconds, lifecycleCpuMs, lifecycleAllocated,
                os = RuntimeInformation.OSDescription, runtime = RuntimeInformation.FrameworkDescription,
                binary, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary)))
            }));
            passed = true;
        }
        finally
        {
            foreach (var db in connections) db?.Dispose();
            if (passed) Directory.Delete(directory, true);
            else Console.Error.WriteLine("Preserved resource diagnostic files: " + directory);
        }
    }
}
