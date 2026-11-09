using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace LiteDB.Fuzz;

// Scratch-only admission probe; product sources are built at the matrix's exact revision.
internal static class MacosCutProbe
{
    internal static async Task<int> Run(string root)
    {
        Directory.CreateDirectory(root);
        var results = new List<object>();
        foreach (var holdReader in new[] { false, true })
        {
            var directory = Path.Combine(root, holdReader ? "held-reader" : "no-reader");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "shared.db");
            using (var setup = Open(file))
                setup.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "control", ["padding"] = new string('x', 131072) });
            LiteDatabase reader = null;
            IEnumerator<BsonDocument> cursor = null;
            try
            {
                if (holdReader)
                {
                    var readerEngine = new SharedEngine(new Engine.EngineSettings { Filename = file });
                    reader = new LiteDatabase(readerEngine);
                    cursor = reader.GetCollection("rows").FindAll().GetEnumerator();
                    if (!cursor.MoveNext()) throw new Exception("Reader did not pin control row");
                    if (readerEngine.MappedReaderTable.LiveVersions().Length == 0)
                        throw new Exception("Reader did not retain an admitted mapped lease");
                }
                var ledger = Path.Combine(directory, "worker-0.jsonl");
                var start = new ProcessStartInfo("dotnet")
                {
                    RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false
                };
                foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location,
                    "--child", "shared", "--database", file, "--ledger", ledger,
                    "--worker-id", "0", "--seed", "641424", "--count", "400", "--crash-at", "22" })
                    start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                await process.WaitForExitAsync(timeout.Token);
                await File.WriteAllTextAsync(Path.Combine(directory, "stdout.log"), await stdout);
                var error = await stderr;
                await File.WriteAllTextAsync(Path.Combine(directory, "stderr.log"), error);
                var marker = File.Exists(ledger + ".crash") ? File.ReadAllText(ledger + ".crash") : null;
                if (process.ExitCode == 0) throw new Exception("Child unexpectedly succeeded");
                if (!holdReader && marker != "22|checkpoint-after-page-write")
                    throw new Exception("No-reader positive control did not reach page-write cut: " + error);
                if (holdReader && marker == null && !error.Contains("Configured shared-process crash boundary was not reached"))
                    throw new Exception("Held reader failed with another identity: " + error);
                results.Add(new { holdReader, process.ExitCode, marker,
                    missingBoundary = error.Contains("Configured shared-process crash boundary was not reached") });
            }
            finally { cursor?.Dispose(); reader?.Dispose(); }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var verify = Open(file);
                var rows = verify.GetCollection("rows").FindAll().ToArray();
                if (rows.Length != 5 || rows.Single(row => row["_id"].AsInt32 == 1)["value"].AsString != "control")
                    throw new Exception("Cold reopen lost acknowledged rows");
                foreach (var id in new[] { 10001, 10002, 10003, 10004 })
                    if (!rows.Any(row => row["_id"].AsInt32 == id)) throw new Exception("Missing committed row " + id);
                verify.Checkpoint();
            }
        }
        await File.WriteAllTextAsync(Path.Combine(root, "probe.json"), System.Text.Json.JsonSerializer.Serialize(results));
        return 0;
    }

    private static LiteDatabase Open(string file) => new(new ConnectionString
    { Filename = file, Connection = ConnectionType.Shared });
}
