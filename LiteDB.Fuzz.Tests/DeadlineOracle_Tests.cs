using System.Text.Json;
using LiteDB.Fuzz.SelfTests;
using LiteDB.Vector;
using Xunit;

namespace LiteDB.Fuzz.Tests;

/// <summary>
/// The deadline oracle: a declared per-operation deadline that no other activity refreshes,
/// in-process and through the real runner in a child process.
/// </summary>
[Collection(OracleSelfTestCollection.Name)]
public sealed class DeadlineOracle_Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litedb-deadline-" + Guid.NewGuid().ToString("N"));

    public DeadlineOracle_Tests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Deadline_records_outcomes_and_fails_an_operation_that_returns_after_its_declared_deadline()
    {
        using var context = this.Context();
        context.Oracles.PragmaTimeout = TimeSpan.Zero;
        context.Oracles.DeadlineFloor = TimeSpan.FromMilliseconds(200);
        Assert.Equal(7, context.Deadline("Fast", () => 7, "mode=test"));
        Assert.Throws<InvalidOperationException>(() => context.Deadline("Throws", () => throw new InvalidOperationException()));
        using (var db = new LiteDatabase(new MemoryStream()))
        {
            var rows = db.GetCollection("rows");
            // A documented refusal (marked in the engine) is classified as refused, not threw.
            Assert.Throws<InvalidOperationException>(() => context.Deadline("WhereNearTwice", () =>
                rows.Query().WhereNear("v", new[] { 1f }, 1).WhereNear("v", new[] { 1f }, 1)));
        }
        // A declared deadline replaces the lock-bound default.
        context.Deadline("SlowButDeclared", () => Thread.Sleep(300), declared: TimeSpan.FromSeconds(5));
        Assert.Equal("DEADLINE_ORACLE_SELFTEST_SLOW",
            Assert.Throws<FuzzFailureException>(() => context.Deadline("Slow", () => Thread.Sleep(600))).FailureId);

        var outcomes = File.ReadAllLines(Path.Combine(context.DirectoryPath, FuzzOracleState.OutcomesFile))
            .Select(line => JsonDocument.Parse(line).RootElement).ToArray();
        Assert.Equal(new[] { "ok", "threw", "refused", "ok", "hang" }, outcomes.Select(item => item.GetProperty("outcome").GetString()));
        Assert.Equal("mode=test", outcomes[0].GetProperty("dimension").GetString());
        Assert.Equal("System.InvalidOperationException", outcomes[1].GetProperty("exceptionType").GetString());
    }

    [Fact]
    public async Task A_stalled_actor_fails_its_child_process_although_another_actor_keeps_progressing()
    {
        var options = FuzzOptions.Parse(new[] { "--artifact-dir", _root, "--count", "3", "--seed", "11" });
        var result = Assert.Single(await FuzzProcessRunner.RunEpochsAsync(new DeadlineSelfTestFuzzer(), options, 0));

        Assert.False(result.Passed);
        const string id = "DEADLINE_ORACLE_DEADLINE_SELFTEST_STALLEDACTOR";
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Directory, "run.json")));
        Assert.Equal(id, run.RootElement.GetProperty("failureId").GetString());
        Assert.Equal(id, FuzzArtifacts.ReadReplay(Path.Combine(result.Directory, "replay.json")).FailureId);
        var minimized = FuzzArtifacts.ReadReplay(Path.Combine(result.Directory, "minimized-replay.json"));
        Assert.Equal(id, minimized.FailureId);
        Assert.Equal(2, minimized.Count);

        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Directory, FuzzOracleState.DeadlineFailureFile)));
        var inFlight = report.RootElement.GetProperty("inFlight").EnumerateArray()
            .Select(item => item.GetProperty("Operation").GetString()).ToArray();
        Assert.Equal("StalledActor", inFlight[0]);
        // Actor B completed operations while A was stalled; none of them refreshed A's deadline.
        var outcomes = File.ReadAllLines(Path.Combine(result.Directory, FuzzOracleState.OutcomesFile));
        Assert.True(outcomes.Count(line => line.Contains("\"op\":\"ProgressingActor\"", StringComparison.Ordinal) &&
            line.Contains("\"outcome\":\"ok\"", StringComparison.Ordinal)) >= 5, string.Join("\n", outcomes));
        Assert.Contains(outcomes, line => line.Contains("\"op\":\"StalledActor\"", StringComparison.Ordinal) &&
            line.Contains("\"outcome\":\"hang\"", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(result.Directory, FuzzMarkers.FileName)));
    }

    private FuzzContext Context() =>
        new("oracle-selftest", 1, 1, null, Path.Combine(_root, "run-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
