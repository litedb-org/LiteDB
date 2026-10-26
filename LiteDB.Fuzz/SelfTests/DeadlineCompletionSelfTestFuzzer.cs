using System.Diagnostics;

namespace LiteDB.Fuzz.SelfTests;

/// <summary>Forces an overdue call to finish while its watchdog is recording the failure.</summary>
internal sealed class DeadlineCompletionSelfTestFuzzer : IFuzzTarget
{
    internal const string TargetName = "oracle-deadline-completion-selftest";
    internal const string CompletionObservedFile = "completion-observed.txt";
    internal const string CompletionResultFile = "completion-result.txt";
    public string Name => TargetName;
    public string Description => "Deadline reporting races with synchronous/asynchronous completion (self-test only).";

    public async Task RunAsync(FuzzContext context)
    {
        using var reported = new ManualResetEventSlim();
        var completed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = (context.Seed % 3) switch
        {
            1 => "threw: System.InvalidOperationException: Operation failed after its deadline.",
            2 => "async-completed",
            _ => "returned"
        };
        var handler = context.DeadlineFailureHandler;
        context.DeadlineFailureHandler = async error =>
        {
            File.AppendAllText(Path.Combine(context.DirectoryPath, "handler-calls.txt"), error.FailureId + "\n");
            reported.Set();
            // End also runs after an unintended exception (including a missed report).
            // First prove what the delegate actually did, then wait for the oracle's End.
            var outcome = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (outcome != expected)
                throw new InvalidOperationException($"Expected completion '{expected}', observed '{outcome}'.");
            // End removes the operation before the completion path checks failure ownership.
            // Keep this handler in control until that interleaving actually occurs.
            var wait = Stopwatch.StartNew();
            while (context.Oracles.Watchdog.Snapshot().Length != 0)
            {
                if (wait.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException("The overdue operation did not finish while the watchdog owned failure reporting.");
                await Task.Delay(10);
            }
            File.WriteAllText(Path.Combine(context.DirectoryPath, CompletionObservedFile),
                System.Text.Json.JsonSerializer.Serialize(new { failureId = error.FailureId, outcome }));
            await handler(error);
        };
        while (context.Next())
        {
            if (context.Steps < 2) continue;
            if (context.Seed % 3 == 2)
            {
                await context.DeadlineAsync("Finishes", CompleteAsynchronously, declared: TimeSpan.FromMilliseconds(200));
            }
            else
            {
                context.Deadline("Finishes", CompleteSynchronously, declared: TimeSpan.FromMilliseconds(200));
            }
        }

        void Complete()
        {
            if (!reported.Wait(TimeSpan.FromSeconds(15))) throw new InvalidOperationException("Watchdog did not report.");
            if (context.Seed % 3 == 1) throw new InvalidOperationException("Operation failed after its deadline.");
        }

        void CompleteSynchronously()
        {
            try { Complete(); }
            catch (Exception error)
            {
                RecordCompletion($"threw: {error.GetType().FullName}: {error.Message}");
                throw;
            }
            RecordCompletion("returned");
        }

        async Task CompleteAsynchronously()
        {
            try { await Task.Run(Complete); }
            catch (Exception error)
            {
                RecordCompletion($"async-threw: {error.GetType().FullName}: {error.Message}");
                throw;
            }
            RecordCompletion("async-completed");
        }

        void RecordCompletion(string outcome)
        {
            // Retain unexpected outcomes even if the watchdog handler has not started yet.
            File.WriteAllText(Path.Combine(context.DirectoryPath, CompletionResultFile), outcome);
            completed.SetResult(outcome);
        }
    }
}
