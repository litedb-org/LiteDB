using System.Text.Json;
using LiteDB.ConcurrencyTesting;

namespace LiteDB.Fuzz.Targets;

/// <summary>
/// Random dependency programs (docs/concurrency-explorer.md, "lifetime-chaos"): each step generates
/// a <see cref="LifetimeChaosProgram"/> from (seed, step) only: 2-6 operations on as many threads
/// whose callbacks and input sequences await operations on other threads (nested up to depth 3),
/// with a concurrent Dispose, Rebuild or injected fatal WAL write at a random point, in Direct or
/// Shared mode with every access kind this build has.
/// </summary>
/// <remarks>
/// Oracles (through <see cref="FuzzExplorerHost"/>): Deadline per operation (declared: callbacks,
/// checkpoint and maintenance 60 s; others lock-bound), permitted outcomes from what disturbs each
/// operation's connection or file, Ownership after each judged operation (Shared), ConnectionClean
/// after every dispose, Durable plus an exact cold check of every file, Quiescent at scenario end,
/// FaultReached/FaultDisposed for the fatal write. Evidence class 2 (native threads): the trace holds
/// the program text (deterministic); a failure keeps the step directory with the program, history and
/// environment (<c>explorer-failure.json</c>) and writes <c>evidence.json</c> in the run directory; a
/// replay that does not reproduce is classified, and the finding is kept. Program choices covered by
/// a registered hang/crash finding are withheld at generation (listed in the program text, counted).
/// </remarks>
internal sealed class LifetimeChaosFuzzer : IFuzzTarget
{
    private const string EvidenceFile = "evidence.json";

    public string Name => "lifetime-chaos";
    public string Description => "Random dependency DAGs over 2-6 threads with concurrent Dispose, Rebuild and fatal injection.";

    public Task RunAsync(FuzzContext context)
    {
        var kinds = ExplorerAccessKinds.All.Select(access => access.Name).ToArray();
        context.Metrics["accessKinds"] = string.Join(",", kinds);
        using var host = new FuzzExplorerHost(context);
        ExplorerRun.AuditPageBuffers();
        while (context.Next())
        {
            var program = LifetimeChaosProgram.Generate(context.Seed, context.Steps, kinds, ExplorerKnownFindings.IncludeKnown);
            context.Trace("program", new { lines = program.Lines().ToArray() });
            if (program.Withheld.Count > 0) TransactionInterleavingsFuzzer.Count(context, "withheldByKnownFinding");
            context.Metrics["maxActors"] = Math.Max(context.Metrics.TryGetValue("maxActors", out var max) ? (int)max : 0, program.Nodes.Count);
            var directory = Path.Combine(context.DirectoryPath, "chaos-step-" + context.Steps);
            var result = ExplorerRun.Execute(program.Vector, directory, host, program);
            if (result.Verdict == ExplorerVerdict.Failed && ExplorerKnownFindings.Match(result) == null)
                File.WriteAllText(Path.Combine(context.DirectoryPath, EvidenceFile), System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, evidenceClass = 2, target = Name, seed = context.Seed, step = context.Steps,
                    failureId = result.FailureId, program = program.Lines().ToArray(), artifact = result.ArtifactPath,
                    history = result.HistoryPath,
                    replay = $"dotnet run --project LiteDB.Fuzz -c Release -f net8.0 -- --target {Name} --seed {context.Seed} --count {context.Steps}; " +
                        $"or LITEDB_LIFETIME_CHAOS={context.Seed}:{context.Steps} with the LifetimeChaosReplay test",
                    classification = "unclassified until replayed: schedule-dependent, environment-dependent or harness nondeterminism"
                }, new JsonSerializerOptions { WriteIndented = true }));
            TransactionInterleavingsFuzzer.Record(context, result);
        }
        return Task.CompletedTask;
    }
}
