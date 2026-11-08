using System.Diagnostics;
using LiteDB.Fuzz.Targets;
using Xunit;

namespace LiteDB.Fuzz.Tests;

public sealed class SharedCheckpointClearProcess_Tests
{
    [Theory]
    [InlineData(25, false)] [InlineData(26, false)]
    [InlineData(25, true)] [InlineData(26, true)]
    public async Task Clear_crash_hooks_fire_on_default_and_explicit_legacy_and_recover_twice(int cut, bool legacy)
    {
        var root = Path.Combine(Path.GetTempPath(), "litedb-clear-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "test.db");
        var ledger = Path.Combine(root, "worker.jsonl");
        var settings = new ConnectionString { Filename = file, Connection = ConnectionType.Shared };
        if (legacy) settings.SharedOneFile = false;
        try
        {
            using (var seed = new LiteDatabase(settings))
            {
                seed.GetCollection("rows").EnsureIndex("value");
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "control" });
            }
            using var child = Process.Start(SharedProcessFuzzer.StartInfo(file, ledger, 0, 2947, 30, cut, legacy))!;
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await child.WaitForExitAsync(timeout.Token); }
            finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            await output; await error;
            Assert.NotEqual(0, child.ExitCode);
            Assert.Equal(cut + "|checkpoint-" + (cut == 25 ? "before" : "after") + "-clear", File.ReadAllText(ledger + ".crash"));
            Assert.True(File.Exists(ledger));
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var recovered = new LiteDatabase(settings);
                var rows = recovered.GetCollection("rows");
                Assert.Equal("control", rows.FindById(1)["value"].AsString);
                Assert.Equal(5, rows.Count());
                foreach (var row in rows.FindAll())
                    Assert.Equal(row["_id"], rows.Find(Query.EQ("value", row["value"])).Single()["_id"]);
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
