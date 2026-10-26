namespace LiteDB.Fuzz.SelfTests;

/// <summary>
/// Oracle self-test, selectable by name only (never part of <c>all</c> or <c>--list</c>): actor A
/// stalls inside an operation while actor B keeps completing operations on another thread. The
/// deadline oracle must report A (<c>DEADLINE_*_STALLEDACTOR</c>) although B keeps making progress,
/// and the report must list both actors in flight. <c>LiteDB.Fuzz.Tests</c> runs it through the
/// real runner in a child process.
/// </summary>
internal sealed class DeadlineSelfTestFuzzer : IFuzzTarget
{
    internal const string TargetName = "oracle-deadline-selftest";

    public string Name => TargetName;
    public string Description => "Deliberately stalled actor that the deadline oracle must report (self-test only).";

    public Task RunAsync(FuzzContext context)
    {
        context.Oracles.PragmaTimeout = TimeSpan.Zero;
        context.Oracles.DeadlineFloor = TimeSpan.FromSeconds(1);
        using var never = new ManualResetEventSlim(false);
        while (context.Next())
        {
            context.Deadline("CompletedOperation", () => context.Random.Next(), "selftest");
            if (context.Steps < 2) continue;
            context.Deadline("StalledActor", () =>
            {
                // Actor B keeps completing short operations; none of them may refresh A's deadline.
                var progressing = new Thread(() =>
                {
                    while (!never.IsSet)
                    {
                        context.Deadline("ProgressingActor", () => Thread.Sleep(20), "selftest");
                        context.PulseHeartbeat();
                    }
                }) { IsBackground = true, Name = "selftest progressing actor" };
                progressing.Start();
                never.Wait();
            }, "selftest");
        }
        return Task.CompletedTask;
    }
}
