using System.Diagnostics;
using LiteDB.Tests.Safety;

namespace LiteDB.Fuzz;

/// <summary>
/// The invariant oracles fuzz targets apply after their operations and closes. Probe logic
/// lives in <c>LiteDB.Tests/Safety</c> so xUnit tests can share it; these wrappers turn a
/// violation into a stable, replayable failure id and record per-operation evidence.
/// <list type="bullet">
/// <item><see cref="Deadline{T}"/>: the call runs inline on the caller's thread and completes or
/// throws within the deadline its scenario declared (<c>DEADLINE_&lt;TARGET&gt;_&lt;OP&gt;</c>); one
/// <c>outcomes.jsonl</c> line per call.</item>
/// <item><see cref="ConnectionClean"/> after each dispose (what that connection owned is released),
/// <see cref="Quiescent"/> only at scenario end (no thread, handle, mutex, lease or scratch left),
/// <see cref="ScratchLive"/> while a reader with a spilled sort is live.</item>
/// <item><see cref="Ownership(FuzzContext, SharedEngine, string)"/>: a protected Shared core active or
/// tearing down keeps the writer mutex; releasing it implies the core's teardown completed
/// (latched by the ownership monitor at every release).</item>
/// <item><see cref="Durable"/>: acknowledged effects survive close and cold reopen; known-aborted
/// effects stay absent.</item>
/// <item><see cref="FaultReached"/> and <see cref="FaultDisposed"/>: an injected fault fired, and
/// the call disposed of it as its path declares.</item>
/// </list>
/// None of them consumes fuzz randomness or writes to <c>trace.jsonl</c>.
/// </summary>
internal static class FuzzOracles
{
    /// <param name="declared">
    /// The deadline the scenario declares for this operation; null uses the default for lock-bound
    /// operations, max(3 x TIMEOUT pragma, 15 s). Bulk, rebuild and callback operations declare their own.
    /// </param>
    /// <param name="permitted">
    /// Optional: the outcomes the scenario declares legal for this call (<c>ok</c>, <c>refused</c>,
    /// <c>threw</c>, or a kind with type and LiteDB error code such as <c>threw:LiteDB.LiteException#137</c>).
    /// Written to outcomes.jsonl as <c>permitted</c> for the differential run; the target enforces it.
    /// </param>
    internal static T Deadline<T>(this FuzzContext context, string op, Func<T> call, string dimension = null,
        TimeSpan? declared = null, string[] permitted = null)
    {
        var state = context.Oracles;
        state.ThrowLatched();
        var deadline = state.Deadline(declared);
        var item = state.Watchdog.Begin(op, dimension, context.Steps, deadline);
        var refusals = state.RefusalsOnThread;
        var clock = Stopwatch.StartNew();
        T result;
        try { result = call(); }
        catch (Exception error)
        {
            Finish(state.RefusalsOnThread != refusals ? "refused" : "threw", error);
            throw;
        }
        Finish("ok", null);
        return result;

        void Finish(string outcome, Exception error)
        {
            state.Watchdog.End(item);
            var elapsed = clock.Elapsed;
            var late = elapsed > deadline;
            // The watchdog already recorded an operation it reported as overdue.
            if (!item.Reported) state.WriteOutcome(op, dimension, item.Step, late ? "hang" : outcome, error, elapsed.TotalMilliseconds, permitted);
            // An operation that finally returned or threw after its deadline still missed it.
            if (late)
                throw new FuzzFailureException(FuzzDeadlineFailure.FailureId(context.Target, op),
                    $"Operation {op} ({dimension}) took {elapsed.TotalSeconds:F1} s, beyond its {deadline.TotalSeconds:F0} s deadline" +
                    (error == null ? "." : $", then threw {error.GetType().FullName}: {error.Message}"));
        }
    }

    internal static void Deadline(this FuzzContext context, string op, Action call, string dimension = null,
        TimeSpan? declared = null, string[] permitted = null) =>
        context.Deadline(op, () => { call(); return true; }, dimension, declared, permitted);

    /// <summary>
    /// An asynchronous operation (for example joining workers) under its declared deadline. It starts
    /// on the caller's thread; its continuation may resume elsewhere, which the clock does not care about.
    /// </summary>
    internal static async Task DeadlineAsync(this FuzzContext context, string op, Func<Task> call, string dimension = null,
        TimeSpan? declared = null)
    {
        var state = context.Oracles;
        state.ThrowLatched();
        var deadline = state.Deadline(declared);
        var item = state.Watchdog.Begin(op, dimension, context.Steps, deadline);
        var clock = Stopwatch.StartNew();
        Exception failure = null;
        try { await call(); }
        catch (Exception error) { failure = error; }
        state.Watchdog.End(item);
        var elapsed = clock.Elapsed;
        if (!item.Reported)
            state.WriteOutcome(op, dimension, item.Step, elapsed > deadline ? "hang" : failure == null ? "ok" : "threw",
                failure, elapsed.TotalMilliseconds);
        if (elapsed > deadline)
            throw new FuzzFailureException(FuzzDeadlineFailure.FailureId(context.Target, op),
                $"Operation {op} ({dimension}) took {elapsed.TotalSeconds:F1} s, beyond its {deadline.TotalSeconds:F0} s deadline.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>After <paramref name="connection"/> (a LiteDatabase or engine) was disposed: what it owned is released.</summary>
    internal static ConnectionCleanResult ConnectionClean(this FuzzContext context, object connection, string op = "Dispose")
    {
        var state = context.Oracles;
        state.ThrowLatched();
        var result = ConnectionCleanProbe.Evaluate(connection, state.OwnershipIfWatched);
        state.WriteConnectionClean(op, result);
        Count(context, "connectionCleanChecks");
        if (result.Clean) return result;
        throw new FuzzFailureException($"CONNECTION_CLEAN_{Safe(context.Target)}_{Kind(result.Violations[0])}",
            $"After {op} ({result.Mode}): " + string.Join("; ", result.Violations));
    }

    /// <summary>Only at scenario end, after every participant stopped and every owner of <paramref name="path"/> closed.</summary>
    internal static QuiescentResult Quiescent(this FuzzContext context, string path, string point = "scenario end",
        int allowedThreads = QuiescentProbe.IdleThreadCap)
    {
        var state = context.Oracles;
        state.ThrowLatched();
        var result = QuiescentProbe.Evaluate(path, allowedThreads);
        state.WriteQuiescent(point, result);
        Count(context, "quiescentChecks");
        if (result.Clean) return result;
        throw new FuzzFailureException($"QUIESCENT_{Safe(context.Target)}_{Kind(result.Violations[0])}",
            $"At {point} for {Path.GetFileName(path)}: " + string.Join("; ", result.Violations));
    }

    /// <summary>While a reader whose sort spilled is live: its scratch file exists.</summary>
    internal static void ScratchLive(this FuzzContext context, string path, string point)
    {
        Count(context, "scratchLiveChecks");
        var violation = QuiescentProbe.ScratchLive(path);
        if (violation != null)
            throw new FuzzFailureException($"SCRATCH_LIVE_{Safe(context.Target)}", $"At {point}: {violation}");
    }

    /// <summary>
    /// Point check of the Shared ownership invariant for <paramref name="database"/> (no-op for other
    /// engines); also surfaces violations latched at mutex releases since the last oracle call.
    /// Call <c>context.Oracles.WatchOwnership()</c> before the connection opens to observe its whole life.
    /// </summary>
    internal static OwnershipViolation Ownership(this FuzzContext context, ILiteDatabase database, string point) =>
        ConnectionCleanProbe.EngineOf(database) is SharedEngine shared ? context.Ownership(shared, point) : null;

    internal static OwnershipViolation Ownership(this FuzzContext context, SharedEngine connection, string point)
    {
        var state = context.Oracles;
        var monitor = state.WatchOwnership();
        state.ThrowLatched();
        Count(context, "ownershipChecks");
        var violation = monitor.Evaluate(connection);
        if (violation != null)
            throw new FuzzFailureException($"OWNERSHIP_{Safe(context.Target)}_{violation.Kind}", $"At {point}: {violation.Detail}");
        return null;
    }

    internal static void Durable(this FuzzContext context, DurableLedger ledger, ILiteDatabase reopened, string point)
    {
        context.Oracles.ThrowLatched();
        var violations = ledger.Verify(reopened);
        Count(context, "durableChecks");
        if (violations.Count == 0) return;
        throw new FuzzFailureException($"DURABLE_{Safe(context.Target)}_{violations[0].Split(':')[0]}",
            $"After {point}: " + string.Join("; ", violations.Take(5)));
    }

    /// <summary>
    /// FaultReached: records whether the injected fault <paramref name="fault"/> fired. A scenario that
    /// exists to exercise it passes <paramref name="required"/>, so a fault that never fires fails the run
    /// instead of leaving the disposition check vacuous. Returns whether it fired.
    /// </summary>
    internal static bool FaultReached(this FuzzContext context, string fault, Exception injected, bool required = false)
    {
        context.Oracles.WriteFault(fault, null, injected != null, null, null);
        if (injected != null) Count(context, "faultsReached");
        if (injected == null && required)
            throw new FuzzFailureException($"FAULT_NOT_REACHED_{Safe(context.Target)}_{Safe(fault)}",
                $"The scenario requires fault {fault} to fire, but it never did.");
        return injected != null;
    }

    /// <summary>
    /// FaultDisposed: the call <paramref name="op"/> disposed of the injected fault as its path declares.
    /// A fault that never fired is not judged (see <see cref="FaultReached"/>).
    /// </summary>
    internal static void FaultDisposed(this FuzzContext context, string op, Exception injected, FaultDisposition declared,
        Exception thrown, IEnumerable<Exception> returned = null, Exception primary = null, bool retried = false)
    {
        if (injected == null) return;
        var observed = FaultDisposedProbe.Observe(injected, thrown, returned, primary, retried);
        var matches = observed != FaultDisposition.None && (declared & observed) == observed;
        context.Oracles.WriteFault(null, op, true, declared, observed);
        Count(context, "faultsDisposedChecked");
        if (matches) return;
        throw new FuzzFailureException($"FAULT_DISPOSED_{Safe(context.Target)}_{Safe(op)}_{Safe(Label(observed))}",
            $"{op} disposed of the injected {injected.GetType().FullName} as {Label(observed)}; its path declares {declared}" +
            (thrown == null ? "." : $" (threw {thrown.GetType().FullName}: {thrown.Message})."));
    }

    internal static string Safe(string value) =>
        new string(value.Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_').ToArray());

    private static string Label(FaultDisposition observed) => observed == FaultDisposition.None ? "Replaced" : observed.ToString();

    private static string Kind(string violation) => Safe(violation.Split(':')[0]);

    private static void Count(FuzzContext context, string metric) =>
        context.Metrics[metric] = (context.Metrics.TryGetValue(metric, out var count) ? (int)count : 0) + 1;
}
