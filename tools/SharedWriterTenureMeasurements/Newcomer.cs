#nullable enable
using System.Diagnostics;
using System.Runtime.InteropServices;
using JsonSerializer = System.Text.Json.JsonSerializer;
using LiteDB;
using LiteDB.Engine;

// Two long-lived OS processes alternate fresh mapped attachments. The idle peer
// has no facade/table reference, so each acquire really wakes a sole holder.
internal static class Newcomer
{
    internal static void Run(string path, bool durable)
    {
        if (Environment.GetEnvironmentVariable("CI") != "true" ||
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "JKamsker/LiteDB" ||
            !OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new InvalidOperationException("Newcomer measurements are Linux x64 fork-CI only.");

        SharedEngine? engine = null;
        LiteDatabase? db = null;
        Console.WriteLine("ready");
        try
        {
            string? line;
            while ((line = Console.ReadLine()) != null && line != "close")
            {
                var parts = line.Split(' ');
                switch (parts[0])
                {
                    case "acquire":
                        if (engine != null) throw new InvalidOperationException("Peer is already attached");
                        var started = Stopwatch.GetTimestamp();
                        engine = new SharedEngine(new EngineSettings
                        { Filename = path, SharedOneFile = true, DurableCommits = durable });
                        db = new LiteDatabase(engine, disposeOnClose: false);
                        if (!db.BeginTrans()) throw new Exception("No fresh writer acquisition");
                        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000;
                        if (!db.GetCollection("docs").Update(Row(int.Parse(parts[1])))) throw new Exception("Missing row");
                        if (!db.Commit()) throw new Exception("No acquired transaction commit");
                        Console.WriteLine(JsonSerializer.Serialize(new { attachToAcquireMicroseconds = elapsed,
                            processId = Environment.ProcessId }));
                        break;
                    case "prepare":
                        // The outgoing peer is now detached. Establish identical positive
                        // checkpoint settings and fresh WAL pressure outside the timed window.
                        if (db == null) throw new Exception("No holder");
                        db.CheckpointSize = 1000;
                        db.Checkpoint();
                        var value = int.Parse(parts[1]);
                        for (var n = 0; n < 30; n++)
                        {
                            // Move the indexed key each time; leave the independently
                            // expected final value in the last committed update.
                            var next = n == 29 ? value : -(value * 30 + n + 1);
                            if (!db.GetCollection("docs").Update(Row(next))) throw new Exception("Missing row");
                        }
                        var walPhysicalBytes = new FileInfo(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-log" + Path.GetExtension(path))).Length;
                        if (walPhysicalBytes <= 50L * 8192) throw new Exception("Insufficient WAL pressure");
                        Console.WriteLine(JsonSerializer.Serialize(new { walPhysicalBytes, updates = 30, checkpoint = db.CheckpointSize }));
                        break;
                    case "detach":
                        db?.Dispose();
                        engine?.Dispose();
                        db = null;
                        engine = null;
                        Console.WriteLine("detached");
                        break;
                    default: throw new ArgumentException(line);
                }
            }
        }
        finally
        {
            db?.Dispose();
            engine?.Dispose();
        }
    }

    private static BsonDocument Row(int value) => new BsonDocument
    { ["_id"] = 1, ["value"] = value, ["payload"] = new string('t', 200) };
}
