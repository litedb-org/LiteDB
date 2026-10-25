using System.Reflection;
using System.Text.Json;
using LiteDB.Fuzz.SelfTests;
using LiteDB.Tests.Safety;
using Xunit;

namespace LiteDB.Fuzz.Tests;

[Collection(OracleSelfTestCollection.Name)]
public sealed class DeadlineOracleCompletion_Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "litedb-deadline-completion-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public async Task A_reported_operation_that_finishes_has_one_failure_owner_and_still_minimizes(int seed)
    {
        var options = FuzzOptions.Parse(new[] { "--artifact-dir", _root, "--count", "3", "--seed", seed.ToString() });
        var runs = await FuzzProcessRunner.RunEpochsAsync(new DeadlineCompletionSelfTestFuzzer(), options, 0)
            .WaitAsync(TimeSpan.FromSeconds(150));
        var result = Assert.Single(runs);
        Assert.False(result.Passed);
        const string id = "DEADLINE_ORACLE_DEADLINE_COMPLETION_SELFTEST_FINISHES";
        var completion = Path.Combine(result.Directory, DeadlineCompletionSelfTestFuzzer.CompletionObservedFile);
        Assert.True(File.Exists(completion), "The operation never finished while the watchdog handler owned failure reporting.");
        Assert.Equal(id, File.ReadAllText(completion));
        Assert.False(File.Exists(Path.Combine(result.Directory, "deadline-handler-error.txt")));
        using var run = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Directory, "run.json")));
        Assert.Equal(id, run.RootElement.GetProperty("failureId").GetString());
        Assert.Equal(new[] { id }, File.ReadAllLines(Path.Combine(result.Directory, "handler-calls.txt")));
        Assert.Single(File.ReadAllLines(Path.Combine(result.Directory, FuzzOracleState.OutcomesFile)));
        var minimized = FuzzArtifacts.ReadReplay(Path.Combine(result.Directory, "minimized-replay.json"));
        Assert.Equal(id, minimized.FailureId);
        Assert.Equal(2, minimized.Count);
        using var trial = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Directory, "minimization", "2",
            FuzzOracleState.DeadlineFailureFile)));
        Assert.Equal("skipped: minimization trial", trial.RootElement.GetProperty("stacks").GetString());
        Assert.False(File.Exists(Path.Combine(result.Directory, "minimization-error.txt")));
    }

    [Fact]
    public async Task Dispose_allows_a_watchdog_callback_to_read_the_oracle_state_while_joining()
    {
        var context = new FuzzContext("dispose-selftest", 1, 1, null, _root);
        using var callbackEntered = new ManualResetEventSlim();
        using var readState = new ManualResetEventSlim();
        var readCompleted = false;
        var state = context.Oracles;
        var watchdog = new DeadlineWatchdog((_, _) =>
        {
            callbackEntered.Set();
            if (readState.Wait(TimeSpan.FromSeconds(5)))
                readCompleted = Task.Run(() => context.Oracles.LongestDeadline).Wait(TimeSpan.FromSeconds(2));
        }, TimeSpan.FromMilliseconds(10));
        typeof(FuzzOracleState).GetField("_watchdog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, watchdog);
        var stop = (ManualResetEventSlim)typeof(DeadlineWatchdog)
            .GetField("_stop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watchdog)!;
        watchdog.Begin("Overdue", "test", 0, TimeSpan.Zero);
        Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
        var disposal = Task.Run(context.Dispose);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => stop.IsSet, TimeSpan.FromSeconds(5)));
        }
        finally { readState.Set(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(readCompleted, "Dispose held the context lock while joining a watchdog that needs it.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
