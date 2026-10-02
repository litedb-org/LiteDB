namespace LiteDB.Fuzz.Targets;

/// <summary>
/// The outcomes each chaos-maintenance call may legally have, declared before it runs (written to
/// outcomes.jsonl as <c>permitted</c>, see contracts/m4-outcomes.md). An entry is a kind
/// (<c>ok</c>, <c>threw</c>, <c>refused</c>) optionally with the exception type and LiteDB error code.
/// Observing anything else fails the run as <c>CHAOS_MAINTENANCE_NOT_PERMITTED_&lt;OP&gt;</c>, unless a
/// registered known finding's predicate claims it.
/// </summary>
internal static class ChaosMaintenanceDeclarations
{
    internal const string Ok = "ok";
    /// <summary>LiteException ENGINE_DISPOSED: the Direct engine (or the gate a rebuild retired) refused the call.</summary>
    internal const string EngineDisposed = "threw:LiteDB.LiteException#137";
    /// <summary>The engine had stopped after the injected I/O failure (EngineState wraps the cause in a new IOException).</summary>
    internal const string StoppedByIo = "threw:System.IO.IOException";
    /// <summary>A Shared connection refused a call admitted after its Dispose started.</summary>
    internal const string SharedDisposed = "refused:System.ObjectDisposedException";
    /// <summary>A Shared rebuild refused while a reader lease is live (docs/shared-mode-safety.md, "Rebuild requires shared readers to close").</summary>
    internal const string SharedRebuildRefused = "refused:LiteDB.LiteException#0";

    /// <summary>
    /// .NET's disposal contract: a Direct call that was already running when the engine (or the gate a
    /// rebuild retired) was disposed under it may fail with ObjectDisposedException. Its object name is
    /// internal (TransactionGate, StreamPool); LockService.EnterTransaction maps the same race to ENGINE_DISPOSED.
    /// </summary>
    internal const string Disposed = "threw:System.ObjectDisposedException";

    internal static readonly string[] SecondDispose = { Ok };

    /// <param name="beforePoint">The call completes before the active thread's forced point.</param>
    internal static string[] Permitted(MaintenancePlan plan, string role, string op, bool beforePoint = false)
    {
        if (role == "maintenance") return Maintenance(plan, op);
        // Before its forced point, active first, no maintenance has started yet.
        if (beforePoint && plan.Order == MaintenanceOrder.ActiveFirst) return new[] { Ok };
        return plan.Shared ? SharedActive(plan, op) : DirectActive(plan, op, beforePoint);
    }

    /// <summary>Does the observed outcome (<paramref name="error"/> null: ok) match one declared entry?</summary>
    internal static bool Matches(string[] permitted, string outcome, Exception error)
    {
        if (permitted == null) return true;
        foreach (var entry in permitted)
        {
            var kind = entry.Split(':')[0];
            if (kind != outcome) continue;
            if (error == null || kind == entry) return true;
            if (entry.Substring(kind.Length + 1) == OpRecord.TypeLabel(error)) return true;
            if (entry.IndexOf('#') < 0 && entry.Substring(kind.Length + 1) == error.GetType().FullName) return true;
        }
        return false;
    }

    private static string[] Maintenance(MaintenancePlan plan, string op)
    {
        var queuedBehindRebuild = !plan.Shared && plan.Active == ActiveKind.Rebuild && plan.Order == MaintenanceOrder.ActiveFirst;
        switch (op)
        {
            case "Dispose":
                // Upstream Dispose discards close errors (FaultDisposition.Discarded); it never throws.
                return new[] { Ok };
            case "FatalWrite":
                // The injected failure propagates. Queued behind a Direct rebuild's exclusive admission, the
                // write is refused before it writes (LockService.EnterTransaction; Issue2965_Tests).
                return queuedBehindRebuild ? new[] { EngineDisposed } : new[] { StoppedByIo };
            case "Rebuild":
                if (plan.Shared)
                    return plan.Active == ActiveKind.Reader && plan.Order == MaintenanceOrder.ActiveFirst
                        ? new[] { Ok, SharedRebuildRefused } : new[] { Ok };
                // A second Direct rebuild waits for the first one's exclusive admission, whose gate the first retires.
                return queuedBehindRebuild ? new[] { EngineDisposed, Disposed } : new[] { Ok };
            default:
                throw new ArgumentException($"No declaration for maintenance call {op}.", nameof(op));
        }
    }

    private static string[] SharedActive(MaintenancePlan plan, string op)
    {
        if (plan.Maintenance != MaintenanceKind.Close) return new[] { Ok };
        // Maintenance first: the call waits for the closing connection's mutex, then is refused.
        if (plan.Order == MaintenanceOrder.MaintenanceFirst) return new[] { SharedDisposed };
        // Active first: an admitted call (also a commit paused in its WAL write) is drained before the
        // close. An idle explicit transaction ends with the connection: its later calls are refused,
        // and Rollback returns false.
        if (op == "TransactionUpsert" || op == "Commit" && !plan.PausesInWal) return new[] { SharedDisposed };
        return new[] { Ok };
    }

    private static string[] DirectActive(MaintenancePlan plan, string op, bool beforePoint)
    {
        var first = plan.Order == MaintenanceOrder.MaintenanceFirst;
        var completion = op is "TransactionUpsert" or "Commit" or "Rollback";
        switch (plan.Maintenance)
        {
            case MaintenanceKind.Close:
                // Maintenance first, Dispose already marked the engine disposed: every call is refused.
                if (first) return new[] { EngineDisposed };
                // A Direct close does not wait for a call in progress. One that resumes on the closed engine fails.
                if (op == "Rebuild") return new[] { Ok, EngineDisposed, Disposed };
                return completion ? new[] { EngineDisposed } : new[] { EngineDisposed, Disposed };
            case MaintenanceKind.Rebuild:
                if (!first) return new[] { Ok };
                // Queued behind the rebuild's exclusive admission: refused when the rebuild retires the gate.
                if (op == "Checkpoint") return new[] { Ok, EngineDisposed, Disposed };
                return op == "Rebuild" ? new[] { EngineDisposed, Disposed } : new[] { EngineDisposed };
            default:
                // Active first, a checkpoint or rebuild holds exclusive admission: the fatal write waits for it.
                if (!first && plan.Active is ActiveKind.Checkpoint or ActiveKind.Rebuild) return new[] { Ok };
                // Maintenance first, calls before the forced point run beside the paused fatal write.
                if (beforePoint) return new[] { Ok };
                // Paused in its WAL write, the active write holds the header lock the fatal write needs to
                // allocate a page or commit: the active commit completes before the fatal write can fail.
                if (plan.PausesInWal) return new[] { Ok };
                if (completion) return new[] { StoppedByIo };
                return op == "Checkpoint" ? new[] { Ok, StoppedByIo, EngineDisposed, Disposed }
                    : new[] { StoppedByIo, EngineDisposed, Disposed };
        }
    }
}
