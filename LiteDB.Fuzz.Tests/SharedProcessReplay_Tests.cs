using LiteDB.Fuzz.Targets;
using Xunit;

namespace LiteDB.Fuzz.Tests;

public sealed class SharedProcessReplay_Tests
{
    [Fact]
    public async Task Native_process_ids_do_not_change_the_replay_contract()
    {
        var root = Path.Combine(Path.GetTempPath(), "litedb-shared-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = new FuzzContext("shared", 3012, 1, null, Path.Combine(root, "first"));
            using var second = new FuzzContext("shared", 3012, 1, null, Path.Combine(root, "second"));
            await new SharedProcessFuzzer().RunAsync(first);
            await new SharedProcessFuzzer().RunAsync(second);
            Assert.Equal(first.TraceHash(), second.TraceHash());
            Assert.Equal(first.Metrics["integrityChecks"], second.Metrics["integrityChecks"]);
            Assert.NotEqual(File.ReadAllText(Path.Combine(first.DirectoryPath, "processes.jsonl")),
                File.ReadAllText(Path.Combine(second.DirectoryPath, "processes.jsonl")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
