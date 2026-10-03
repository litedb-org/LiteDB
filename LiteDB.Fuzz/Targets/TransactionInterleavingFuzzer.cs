using LiteDB.ConcurrencyTesting;

namespace LiteDB.Fuzz.Targets;

internal sealed class TransactionInterleavingFuzzer : IFuzzTarget
{
    public string Name => "transaction-interleavings";
    public string Description => "Systematic forced actor schedules of transaction handles with committed-state and progress oracles.";

    /// <summary>Every schedule of both modes, each plain/encrypted with both outcomes.</summary>
    internal static int CaseCount => (TransactionInterleavingExplorer.ScheduleCount(false) + TransactionInterleavingExplorer.ScheduleCount(true)) * 4;

    public Task RunAsync(FuzzContext context)
    {
        var direct = TransactionInterleavingExplorer.ScheduleCount(false);
        while (context.Next())
        {
            // Enumerate the complete finite matrix, with the seed rotating its start.
            var index = (int)(((long)context.Steps - 1 + (context.Seed & int.MaxValue)) % CaseCount);
            var flat = index / 4;
            var shared = flat >= direct;
            var schedule = shared ? flat - direct : flat;
            var encrypted = (index & 1) != 0;
            var outcome = index / 2 % 2;
            var file = context.StepFile("interleaving-" + context.Steps + ".db");
            context.StepFile("interleaving-" + context.Steps + ".db.other");
            context.StepFile("interleaving-" + context.Steps + ".db.history");
            context.Trace("forced-schedule", new { schedule, name = TransactionInterleavingExplorer.ScheduleName(shared, schedule), shared, encrypted, outcome });
            try { TransactionInterleavingExplorer.Run(file, shared, encrypted, schedule, outcome); }
            catch (Exception error)
            {
                if (error.Data["ExplorerLiveWorker"] is true)
                {
                    // The existing artifact writer snapshots registered files immediately.
                    // Keep originals in this isolated child's directory, but never copy a
                    // database while a retained actor could still be mutating its files.
                    context.Files.Clear();
                    File.WriteAllText(Path.Combine(context.DirectoryPath, "live-worker-retention.txt"),
                        "Original fixtures retained in place. No state-* snapshot is safe before process exit. " + file);
                }
                throw;
            }
            context.ObserveNovelty("interleaving", schedule, shared, encrypted, outcome);
            context.Metrics["completedActorSchedules"] = context.Steps;
        }
        return Task.CompletedTask;
    }
}
