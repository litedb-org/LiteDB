using System.Diagnostics;
using System.Text.Json;
if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") throw new Exception("CI only");
var results = new List<object>();
var bytes = new byte[16384];
new Random(42).NextBytes(bytes);
for (var round = 0; round < 6; round++)
foreach (var durable in new[] { false, true })
foreach (var mode in round % 2 == 0 ? new[] { 0, 1, 2 } : new[] { 2, 1, 0 })
{
    var asyncHandle = mode != 0;
    var nativeEvent = mode == 2;
    var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wal");
    try
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 8192,
            FileOptions.RandomAccess | (asyncHandle ? FileOptions.Asynchronous : FileOptions.None));
        using var writer = new LiteDB.Engine.WalFileWrite(file);
        // Warm runtime path and reuse the same extent in every mode.
        for (var i = 0; i < 100; i++) file.Write(bytes);
        file.Flush(true);
        file.Position = 0;
        long write = 0, length = 0, flush = 0;
        var alloc = GC.GetTotalAllocatedBytes(true);
        var completions = ThreadPool.CompletedWorkItemCount;
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 5000; i++)
        {
            var stamp = Stopwatch.GetTimestamp();
            _ = file.Length;
            length += Stopwatch.GetTimestamp() - stamp;
            stamp = Stopwatch.GetTimestamp();
            if (nativeEvent) writer.Write(bytes, 0, bytes.Length);
            else file.Write(bytes);
            write += Stopwatch.GetTimestamp() - stamp;
            stamp = Stopwatch.GetTimestamp();
            file.Flush(durable);
            flush += Stopwatch.GetTimestamp() - stamp;
        }
        var elapsed = Stopwatch.GetTimestamp() - start;
        var allocated = GC.GetTotalAllocatedBytes(true) - alloc;
        var completed = ThreadPool.CompletedWorkItemCount - completions;
        file.Position = 0;
        var check = new byte[bytes.Length];
        for (var i = 0; i < 5000; i++) { file.ReadExactly(check); if (!check.AsSpan().SequenceEqual(bytes)) throw new Exception("bytes differ"); }
        results.Add(new { round, durable, asyncHandle, nativeEvent, elapsed, write, length, flush, allocated, completed, frequency = Stopwatch.Frequency });
    }
    finally { File.Delete(path); }
}
File.WriteAllText(args[0], JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
