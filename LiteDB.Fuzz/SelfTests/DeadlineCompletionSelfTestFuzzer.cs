using System.Diagnostics;

namespace LiteDB.Fuzz.SelfTests;

/// <summary>Forces an overdue call to finish while its watchdog is recording the failure.</summary>
internal sealed class DeadlineCompletionSelfTestFuzzer : IFuzzTarget
{
    internal const string TargetName = "oracle-deadline-completion-selftest";
    internal const string CompletionObservedFile = "completion-observed.txt";
    public string Name => TargetName;
    public string Description => "Deadline reporting races with synchronous/asynchronous completion (self-test only).";

    public async Task RunAsync(FuzzContext context)
    {
        using var reported = new ManualResetEventSlim();
        var handler = context.DeadlineFailureHandler;
        context.DeadlineFailureHandler = async error =>
        {
            File.AppendAllText(Path.Combine(context.DirectoryPath, "handler-calls.txt"), error.FailureId + "\n");
            reported.Set();
            // End removes the operation before the completion path checks failure ownership.
            // Keep this handler in control until that interleaving actually occurs.
            var wait = Stopwatch.StartNew();
            while (context.Oracles.Watchdog.Snapshot().Length != 0)
            {
                if (wait.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException("The overdue operation did not finish while the watchdog owned failure reporting.");
                await Task.Delay(10);
            }
            File.WriteAllText(Path.Combine(context.DirectoryPath, CompletionObservedFile), error.FailureId);
            await handler(error);
        };
        while (context.Next())
        {
            if (context.Steps < 2) continue;
            if (context.Seed % 3 == 2)
            {
                await context.DeadlineAsync("Finishes", () => Task.Run(Complete), declared: TimeSpan.FromMilliseconds(200));
            }
            else
            {
                context.Deadline("Finishes", Complete, declared: TimeSpan.FromMilliseconds(200));
            }
        }

        void Complete()
        {
            if (!reported.Wait(TimeSpan.FromSeconds(15))) throw new InvalidOperationException("Watchdog did not report.");
            if (context.Seed % 3 == 1) throw new InvalidOperationException("Operation failed after its deadline.");
        }
    }
}
