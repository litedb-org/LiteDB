using System.Text.Json;
using LiteDB.ConcurrencyTesting;

namespace LiteDB.Fuzz.Targets;

/// <summary>
/// Forced actor schedules of the general concurrency explorer (docs/concurrency-explorer.md): each
/// step runs one schedule vector (scenario, variant, seed bit, mode, access kind, maintenance,
/// callback, process, encryption) from the full matrix of every scenario and every access kind
/// this build has, visiting the matrix from a seed-chosen start with the dimensions as the fastest
/// digits (<see cref="ExplorerSelection.Rotating"/>); vectors that do not apply are skipped within the step.
/// </summary>
/// <remarks>
/// Oracles (through <see cref="FuzzExplorerHost"/>): Deadline per actor operation (declared per
/// operation class: lock-bound max(3 x TIMEOUT, 15 s), callbacks/rebuild/close 60 s), permitted
/// outcomes per operation, Ownership after each judged operation (Shared), ConnectionClean after
/// every dispose, Durable and an exact cold check on reopen, Quiescent at scenario end,
/// FaultReached/FaultDisposed for the fatal maintenance. Evidence class 1: a vector's scheduling
/// decisions are recorded and replay; the trace holds only the vector and whether it applied
/// (deterministic). Not-applicable vectors (dimension points outside a mode's contract, access
/// kinds this revision lacks) and vectors excluded by a registered hang/crash finding are counted
/// and traced, never passed. A failure matching a registered known finding is appended to
/// <c>known-findings.jsonl</c> with its retained step directory and the campaign continues;
/// anything else fails the run with its id, keeping the step's fixture, history,
/// <c>explorer-failure.json</c> and <c>waitgraph.txt</c>.
/// </remarks>
internal sealed class TransactionInterleavingsFuzzer : IFuzzTarget
{
    internal const string KnownFindingsFile = "known-findings.jsonl";

    public string Name => "transaction-interleavings";
    public string Description => "Forced actor schedules of the concurrency explorer across all dimensions.";

    public Task RunAsync(FuzzContext context)
    {
        var kinds = ExplorerAccessKinds.All.Select(access => access.Name).ToArray();
        var size = ExplorerSelection.Count(kinds);
        context.Metrics["matrixVectors"] = size;
        context.Metrics["accessKinds"] = string.Join(",", kinds);
        using var host = new FuzzExplorerHost(context);
        // One applicable vector per step: not-applicable and excluded vectors are traced, counted and skipped
        // (both are decided from the vector alone, so the cursor, and every step's vector, is deterministic).
        var cursor = 0;
        while (context.Next())
        {
            for (var attempts = 0; attempts < size; attempts++)
            {
                var vector = ExplorerSelection.Rotating(context.Seed, cursor++, kinds);
                var excluded = ExplorerKnownFindings.Excluding(vector);
                context.Trace("vector", new { vector = vector.ToString(), excludedBy = excluded?.Id });
                if (excluded != null)
                {
                    Count(context, "excludedByKnownFinding");
                    continue;
                }
                var directory = Path.Combine(context.DirectoryPath, "explorer-step-" + context.Steps);
                var result = ExplorerRun.Execute(vector, directory, host);
                Record(context, result);
                if (result.Verdict != ExplorerVerdict.NotApplicable) break;
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Counts the verdict; continues past a registered finding; throws for anything else.</summary>
    internal static void Record(FuzzContext context, ExplorerResult result)
    {
        // Deterministic features only (the vector and whether it applied), never a timing-dependent verdict.
        context.ObserveNovelty("explorer", result.Vector.Scenario, result.Vector.Configuration.Signature,
            result.Verdict == ExplorerVerdict.NotApplicable);
        switch (result.Verdict)
        {
            case ExplorerVerdict.NotApplicable:
                Count(context, "notApplicable");
                return;
            case ExplorerVerdict.Passed:
                Count(context, "passed");
                if (Directory.Exists(result.Directory)) Directory.Delete(result.Directory, true);
                return;
        }
        var known = ExplorerKnownFindings.Match(result);
        if (known != null)
        {
            Count(context, "knownFindings");
            File.AppendAllText(Path.Combine(context.DirectoryPath, KnownFindingsFile), System.Text.Json.JsonSerializer.Serialize(new
            {
                step = context.Steps, finding = known.Id, failureId = result.FailureId, fingerprint = result.Fingerprint,
                vector = result.Vector.ToString(), evidenceClass = result.EvidenceClass, artifact = result.ArtifactPath
            }) + "\n");
            return;
        }
        throw new FuzzFailureException(FuzzExplorerHost.Qualified(context.Target, result.FailureId),
            $"{result.Vector}: {result.Failure?.Message} (artifact {result.ArtifactPath})");
    }

    internal static void Count(FuzzContext context, string metric) =>
        context.Metrics[metric] = (context.Metrics.TryGetValue(metric, out var count) ? (int)count : 0) + 1;
}
