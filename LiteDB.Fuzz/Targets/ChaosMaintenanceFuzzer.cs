using System.Runtime.CompilerServices;
using LiteDB.Engine;
using LiteDB.Tests.Safety;
using LiteDB.Utils;

namespace LiteDB.Fuzz.Targets;

/// <summary>
/// The maintenance dimension of chaos as its own target, so chaos's draws and trace stay unchanged:
/// a close (Dispose), rebuild or fatal I/O failure on one thread, forced against an operation in
/// progress on another thread (bulk insert/upsert with a pausing lazy input, a reader between rows,
/// an explicit transaction between calls, a checkpoint, a rebuild), in Direct and Shared mode, with
/// either side first. See <see cref="ChaosMaintenanceScenario"/> for the forced points and
/// <see cref="ChaosMaintenanceDeclarations"/> for the declared outcome sets.
/// </summary>
/// <remarks>
/// Oracles: every call runs under a declared 60 s <see cref="FuzzOracles.Deadline{T}"/> with its
/// permitted outcomes; each Dispose is followed by <see cref="FuzzOracles.ConnectionClean"/>, each
/// scenario ends with <see cref="FuzzOracles.Quiescent"/> after every participant stopped; Shared
/// scenarios watch <see cref="FuzzOracles.Ownership(FuzzContext, ILiteDatabase, string)"/> for the
/// whole run and check it after seeding and after the race; a cold reopen checks
/// <see cref="FuzzOracles.Durable"/> (acknowledged present, known-aborted absent) and that each
/// uncertain write is all-or-nothing; the injected WAL failure checks FaultReached and that the
/// write propagated it. Evidence class: the forced points order the threads (class 1); which side
/// wins once both run is native (class 2), kept in maintenance-history.jsonl, never in trace.jsonl.
/// </remarks>
internal sealed class ChaosMaintenanceFuzzer : IFuzzTarget
{
    private static int _negativeShares;
    private static int _positiveShares;

    public string Name => "chaos-maintenance";
    public string Description => "Dispose, rebuild and fatal I/O failure forced against active bulk, reader, transaction, checkpoint and rebuild calls (Direct and Shared).";

    public Task RunAsync(FuzzContext context)
    {
        // The ownership monitor and the marker observer must exist before any connection opens.
        context.Oracles.WatchOwnership();
        using var evidence = new ChaosMaintenanceEvidence(context);
        PageBuffer.FinalizedInUse = count =>
        {
            if (count < 0) Interlocked.Increment(ref _negativeShares);
            else Interlocked.Increment(ref _positiveShares);
        };
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (context.Next())
            {
                var plan = MaintenancePlan.Draw(context.Random);
                context.Trace("scenario", plan.TraceDetail);
                kinds.Add(plan.Dimension);
                this.RunStep(context, plan, evidence);
            }
        }
        finally
        {
            Collect();
            PageBuffer.FinalizedInUse = null;
        }
        context.Metrics["maintenanceDimensions"] = kinds.Count;
        return Task.CompletedTask;
    }

    private void RunStep(FuzzContext context, MaintenancePlan plan, ChaosMaintenanceEvidence evidence)
    {
        var file = context.StepFile($"maintenance-{context.Steps}.db");
        Collect();
        var sharesBefore = (Volatile.Read(ref _negativeShares), Volatile.Read(ref _positiveShares));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (ledger, uncertain, history) = RaceAndClose(context, plan, file, evidence);
        Collect();
        var negative = Volatile.Read(ref _negativeShares) - sharesBefore.Item1;
        var positive = Volatile.Read(ref _positiveShares) - sharesBefore.Item2;
        history["finalizedNegative"] = negative;
        history["finalizedPositive"] = positive;
        if (negative + positive > 0)
        {
            var detail = $"{negative} page buffer(s) finalized with a negative and {positive} with a positive share count ({plan.Dimension}).";
            // The engine closed (Dispose or fatal stop) while the active Direct call was still running.
            var closedUnderCall = !plan.Shared && plan.Maintenance != MaintenanceKind.Rebuild;
            if (!closedUnderCall) throw new FuzzFailureException("CHAOS_MAINTENANCE_PAGE_SHARE_COUNT", detail);
            Known(history, ChaosMaintenanceFindings.CloseReleasesActiveCallPages);
            ChaosMaintenanceFindings.Hit(context, evidence, ChaosMaintenanceFindings.CloseReleasesActiveCallPages, detail);
        }
        ColdReopen(context, plan, file, ledger, uncertain);
        context.Quiescent(file, "scenario end");
        history["elapsedMs"] = Math.Round(clock.Elapsed.TotalMilliseconds, 1);
        evidence.WriteHistory(history);
    }

    /// <summary>Everything that holds a reference to the scenario's connection lives here, so a forced GC afterwards finalizes its buffers.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DurableLedger, List<UncertainWrite>, Dictionary<string, object>) RaceAndClose(
        FuzzContext context, MaintenancePlan plan, string file, ChaosMaintenanceEvidence evidence)
    {
        using var scenario = new ChaosMaintenanceScenario(context, plan, file);
        scenario.Open();
        if (plan.Shared) context.Ownership(scenario.Database, "after seeding");
        scenario.Run();
        var history = new Dictionary<string, object>
        {
            ["step"] = context.Steps, ["plan"] = plan.TraceDetail, ["schedule"] = scenario.Schedule,
            ["outcomes"] = scenario.Records.Select(record => new { record.Op, outcome = record.Label, permitted = record.Permitted }).ToArray()
        };
        MarkSituations(plan, scenario.Schedule);
        // An outcome outside its declared set fails the run unless a known finding's precise predicate claims it.
        foreach (var record in scenario.Records.Where(record => !record.IsPermitted))
        {
            if (!ChaosMaintenanceFindings.ClosedUnderRunningCall(plan, record))
                throw new FuzzFailureException("CHAOS_MAINTENANCE_NOT_PERMITTED_" + FuzzOracles.Safe(record.Op),
                    $"{record.Op} ended {record.Label}; declared [{string.Join(", ", record.Permitted)}] ({plan.Dimension}; schedule " +
                    $"{string.Join(" > ", scenario.Schedule)}): {record.Error}");
            Known(history, ChaosMaintenanceFindings.CloseReleasesActiveCallPages);
            ChaosMaintenanceFindings.Hit(context, evidence, ChaosMaintenanceFindings.CloseReleasesActiveCallPages,
                $"{record.Op} failed the page-ownership check after the engine was closed under it ({plan.Dimension}): {record.Error.Message}");
        }
        if (plan.Maintenance == MaintenanceKind.Fatal &&
            context.FaultReached("chaos-maintenance-wal-write", scenario.InjectedFault, required: scenario.FaultRequired))
        {
            var write = scenario.Records.Single(record => record.Op == "FatalWrite");
            context.FaultDisposed("FatalWrite", scenario.InjectedFault, FaultDisposition.Propagated, write.Error);
        }
        JudgeSecondDispose(context, plan, scenario, evidence, history);
        if (plan.Shared) context.Ownership(scenario.Database, "after the race");
        CloseConnection(context, plan, scenario, evidence, history);
        return (scenario.Ledger, scenario.Uncertain, history);
    }

    /// <summary>Close (maintenance first): the caller's own Dispose returned while the first close still held the files.</summary>
    private static void JudgeSecondDispose(FuzzContext context, MaintenancePlan plan, ChaosMaintenanceScenario scenario,
        ChaosMaintenanceEvidence evidence, Dictionary<string, object> history)
    {
        var second = scenario.SecondDispose;
        if (second == null) return;
        history["secondDispose"] = new { second.FirstStillClosing, second.OpenHandles, second.HandlesUnknown, second.MutexHeld };
        // Returned while the first close was still paused and still held what Dispose must have released.
        if (second.Record.Error == null && second.FirstStillClosing && (second.OpenHandles.Length > 0 || second.MutexHeld))
        {
            var id = plan.Shared ? ChaosMaintenanceFindings.SharedSecondDisposeReturnsEarly : ChaosMaintenanceFindings.DirectSecondDisposeReturnsEarly;
            Known(history, id);
            ChaosMaintenanceFindings.Hit(context, evidence, id,
                $"A second Dispose returned while the first ({plan.Mode}) was paused in its close checkpoint; open: " +
                string.Join(", ", second.OpenHandles.Select(Path.GetFileName)) + (second.MutexHeld ? "; Shared mutex held" : ""));
            return;
        }
        context.ConnectionClean(scenario.Database, "SecondDispose");
    }

    private static void CloseConnection(FuzzContext context, MaintenancePlan plan, ChaosMaintenanceScenario scenario,
        ChaosMaintenanceEvidence evidence, Dictionary<string, object> history)
    {
        var db = scenario.Database;
        if (plan.Maintenance != MaintenanceKind.Close)
        {
            context.Deadline("Dispose", () => db.Dispose(), plan.Dimension, ChaosMaintenanceScenario.OpDeadline,
                new[] { ChaosMaintenanceDeclarations.Ok });
            context.ConnectionClean(db);
            return;
        }
        // Every participant stopped; a Direct engine that a Dispose closed must still be closed.
        var reopened = !plan.Shared && !ConnectionCleanProbe.Evaluate(db).EngineDisposed;
        var rebuildReturned = scenario.Records.Any(record => record.Op == "Rebuild" && record.Error == null);
        if (reopened && plan.Active == ActiveKind.Rebuild && rebuildReturned)
        {
            Known(history, ChaosMaintenanceFindings.RebuildReopensAfterDispose);
            ChaosMaintenanceFindings.Hit(context, evidence, ChaosMaintenanceFindings.RebuildReopensAfterDispose,
                $"Rebuild completed and left the engine open after a concurrent Dispose ({plan.Dimension}).");
            db.Dispose();
        }
        context.ConnectionClean(db, "Dispose (all participants stopped)");
    }

    /// <summary>Cold reopen (Direct, no hooks): acknowledged present, known-aborted absent, uncertain writes all-or-nothing.</summary>
    private static void ColdReopen(FuzzContext context, MaintenancePlan plan, string file, DurableLedger ledger,
        List<UncertainWrite> uncertain)
    {
        var db = new LiteDatabase(new LiteEngine(new EngineSettings { Filename = file }));
        try
        {
            context.Durable(ledger, db, "cold reopen");
            foreach (var group in uncertain)
            {
                var actual = group.Ids.Select(id => db.GetCollection(group.Collection).FindById(id)).ToArray();
                if (!Same(actual, group.Before) && !Same(actual, group.After))
                    throw new FuzzFailureException("CHAOS_MAINTENANCE_PARTIAL_WRITE",
                        $"An uncertain {group.Collection} write is partly visible after cold reopen ({plan.Dimension}).");
            }
            db.Checkpoint();
            DatabaseIntegrityVerifier.Verify(context, file);
        }
        finally { db.Dispose(); }
        context.ConnectionClean(db, "Dispose (cold reopen)");
    }

    private static void MarkSituations(MaintenancePlan plan, List<string> schedule)
    {
        // The paused side was still inside its call when the other side engaged.
        if (plan.Order == MaintenanceOrder.ActiveFirst && schedule.Contains("active-paused") &&
            schedule.Any(edge => edge.StartsWith("maintenance-engaged", StringComparison.Ordinal)))
        {
            if (plan.Maintenance == MaintenanceKind.Close) Reachability.Sometimes("situation:chaos-maintenance-close-overlaps-active-op");
            if (plan.Maintenance == MaintenanceKind.Rebuild) Reachability.Sometimes("situation:chaos-maintenance-rebuild-overlaps-active-op");
            if (plan.Maintenance == MaintenanceKind.Fatal) Reachability.Sometimes("situation:chaos-maintenance-fatal-overlaps-active-op");
            if (plan.Shared) Reachability.Sometimes("situation:chaos-maintenance-shared-overlap");
        }
        if (plan.Order == MaintenanceOrder.MaintenanceFirst && schedule.Contains("maintenance-paused") &&
            schedule.Any(edge => edge.StartsWith("active-engaged", StringComparison.Ordinal)))
            Reachability.Sometimes("situation:chaos-maintenance-op-arrives-during-maintenance");
    }

    private static void Known(Dictionary<string, object> history, string id)
    {
        if (!history.TryGetValue("knownFindings", out var list)) history["knownFindings"] = list = new List<string>();
        ((List<string>)list).Add(id);
    }

    private static bool Same(BsonDocument[] actual, BsonDocument[] expected) => actual.Zip(expected, (left, right) =>
        left == null || right == null ? left == null && right == null
            : BsonSerializer.Serialize(left).SequenceEqual(BsonSerializer.Serialize(right))).All(equal => equal);

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
