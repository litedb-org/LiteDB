using LiteDB.Utils;

namespace LiteDB.Fuzz.Targets;

/// <summary>
/// Known findings on dev that chaos-maintenance reaches (registered in Corpus/known-findings.json,
/// status known). Each has a precise predicate in the target; a hit is recorded in the run's
/// <c>known-findings-hit.jsonl</c> (not trace.jsonl) with a <c>situation:</c> marker and the run
/// continues. With <c>LITEDB_FUZZ_STRICT_KNOWN=1</c> a hit fails the run with the registered id.
/// </summary>
internal static class ChaosMaintenanceFindings
{
    internal const string StrictVariable = "LITEDB_FUZZ_STRICT_KNOWN";
    internal const string HitsFile = "known-findings-hit.jsonl";
    internal const string HistoryFile = "maintenance-history.jsonl";

    /// <summary>
    /// Direct: a Dispose that ran while Rebuild held exclusive admission returned, then Rebuild
    /// reopened the engine (LiteEngine.Close returns early once disposed; Rebuild reopens
    /// unconditionally). The disposed connection serves calls and keeps its files open.
    /// </summary>
    internal const string RebuildReopensAfterDispose = "CHAOS_MAINTENANCE_KNOWN_REBUILD_REOPENS_AFTER_DISPOSE";

    /// <summary>
    /// Direct: a second Dispose, concurrent with a first one paused inside its close, returns while
    /// the first still holds the database files open (LiteEngine.Close returns at once when already
    /// disposed).
    /// </summary>
    internal const string DirectSecondDisposeReturnsEarly = "CHAOS_MAINTENANCE_KNOWN_DIRECT_SECOND_DISPOSE_RETURNS_EARLY";

    /// <summary>
    /// Shared: a second Dispose, concurrent with a first one paused inside its final close, returns
    /// while the first still holds the database files and the Shared mutex (SharedEngine.Dispose
    /// returns at once when <c>_disposed</c> is set). Contradicts docs/shared-mode-safety.md
    /// ("Dispose returns only after it: a disposed connection holds no mutex"). Reproduced by
    /// LiteDB.Tests SharedConcurrentDisposeKnownFinding_Tests.
    /// </summary>
    internal const string SharedSecondDisposeReturnsEarly = "CHAOS_MAINTENANCE_KNOWN_SHARED_SECOND_DISPOSE_RETURNS_EARLY";

    /// <summary>
    /// Direct: closing the engine (Dispose, or the stop after a fatal I/O failure) under a call that
    /// is still running releases that call's page buffers from the closing thread. The running call
    /// then uses buffers it no longer owns: TESTING builds' page-ownership check fails it with
    /// INVALID_DATAFILE_STATE (999) "page buffer ownership was transferred to disk", and a buffer
    /// can reach finalization with a share count other than zero (negative: released twice; without
    /// PageBuffer.FinalizedInUse the finalizer's ENSURE would terminate the process, so the target
    /// installs that observer and counts them per scenario).
    /// </summary>
    internal const string CloseReleasesActiveCallPages = "CHAOS_MAINTENANCE_KNOWN_CLOSE_RELEASES_ACTIVE_CALL_PAGES";

    /// <summary>The page-ownership check's messages (BasePage.EnsurePageOwnership, TESTING builds only).</summary>
    private static readonly string[] OwnershipMessages =
    {
        "page buffer ownership was transferred to disk", "page belongs to a recycled cache frame", "page belongs to a cleared snapshot"
    };

    internal static bool Strict => Environment.GetEnvironmentVariable(StrictVariable) == "1";

    /// <summary>
    /// The precise predicate of <see cref="CloseReleasesActiveCallPages"/> for a call outside its
    /// declared set: Direct, the engine was closed (Dispose or fatal stop) while the active thread's
    /// call was running past its forced point, and the call failed the page-ownership check.
    /// </summary>
    internal static bool ClosedUnderRunningCall(MaintenancePlan plan, OpRecord record) =>
        !plan.Shared && plan.Maintenance != MaintenanceKind.Rebuild && plan.Order == MaintenanceOrder.ActiveFirst &&
        record.Role == "active" && record.Error is LiteException { ErrorCode: LiteException.INVALID_DATAFILE_STATE } error &&
        OwnershipMessages.Any(message => error.Message.Contains(message, StringComparison.Ordinal));

    internal static void Hit(FuzzContext context, ChaosMaintenanceEvidence evidence, string id, string detail)
    {
        switch (id)
        {
            case RebuildReopensAfterDispose:
                Reachability.Sometimes("situation:chaos-maintenance-known-rebuild-reopens-after-dispose");
                break;
            case DirectSecondDisposeReturnsEarly:
                Reachability.Sometimes("situation:chaos-maintenance-known-direct-second-dispose-returns-early");
                break;
            case SharedSecondDisposeReturnsEarly:
                Reachability.Sometimes("situation:chaos-maintenance-known-shared-second-dispose-returns-early");
                break;
            case CloseReleasesActiveCallPages:
                Reachability.Sometimes("situation:chaos-maintenance-known-close-releases-active-call-pages");
                break;
            default:
                throw new ArgumentException($"Unregistered known finding {id}.", nameof(id));
        }
        evidence.WriteHit(id, detail);
        var key = "knownFinding:" + id;
        context.Metrics[key] = (context.Metrics.TryGetValue(key, out var count) ? (int)count : 0) + 1;
        if (Strict) throw new FuzzFailureException(id, detail);
    }
}

/// <summary>The run's class-2 evidence: one history line per scenario and one line per known-finding hit.</summary>
internal sealed class ChaosMaintenanceEvidence : IDisposable
{
    private readonly FuzzContext _context;
    private readonly StreamWriter _history;
    private StreamWriter _hits;

    internal ChaosMaintenanceEvidence(FuzzContext context)
    {
        _context = context;
        _history = Create(ChaosMaintenanceFindings.HistoryFile);
    }

    internal void WriteHistory(object line) => _history.WriteLine(System.Text.Json.JsonSerializer.Serialize(line));

    internal void WriteHit(string id, string detail)
    {
        _hits ??= Create(ChaosMaintenanceFindings.HitsFile);
        _hits.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            target = _context.Target, seed = _context.Seed, step = _context.Steps, failureId = id, detail
        }));
    }

    private StreamWriter Create(string name) =>
        new(Path.Combine(_context.DirectoryPath, name), false) { AutoFlush = true, NewLine = "\n" };

    public void Dispose()
    {
        _history.Dispose();
        _hits?.Dispose();
    }
}
