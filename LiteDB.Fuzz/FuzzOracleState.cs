using LiteDB.Tests.Safety;
using LiteDB.Utils;

namespace LiteDB.Fuzz;

/// <summary>
/// Per-run state of the invariant oracles (<see cref="FuzzOracles"/>): the deadline watchdog, the
/// evidence files (<c>outcomes.jsonl</c>, <c>connection-clean.jsonl</c>, <c>quiescent.jsonl</c>,
/// <c>faults.jsonl</c>), the reachability observer that classifies refusals, and the ownership
/// monitor whose latched violations fail the run at the next oracle call or at scenario end.
/// None of it consumes fuzz randomness or writes to <c>trace.jsonl</c>.
/// </summary>
internal sealed class FuzzOracleState : IDisposable
{
    internal const string OutcomesFile = "outcomes.jsonl";
    internal const string ConnectionCleanFile = "connection-clean.jsonl";
    internal const string QuiescentFile = "quiescent.jsonl";
    internal const string FaultsFile = "faults.jsonl";
    internal const string DeadlineFailureFile = "deadline-failure.json";
    [ThreadStatic] private static int _refusalsOnThread;

    private readonly FuzzContext _context;
    private readonly object _writeLock = new();
    private readonly Dictionary<string, StreamWriter> _writers = new(StringComparer.Ordinal);
    private DeadlineWatchdog _watchdog;
    private OwnershipMonitor _ownership;
    private bool _disposed;

    internal FuzzOracleState(FuzzContext context)
    {
        _context = context;
        Reachability.Observer = this.OnMarker;
    }

    /// <summary>The TIMEOUT pragma of the target's connections; the lock-bound default deadline derives from it.</summary>
    internal TimeSpan PragmaTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Oracle self-tests only: replaces the 15 s floor of the lock-bound default.</summary>
    internal TimeSpan? DeadlineFloor { get; set; }

    /// <summary>Longest deadline handed out, so minimization trials outlive a reproduced DEADLINE failure.</summary>
    internal TimeSpan LongestDeadline { get; private set; }

    internal int RefusalsOnThread => _refusalsOnThread;

    internal string Target => _context.Target;

    internal OwnershipMonitor OwnershipIfWatched => _ownership;

    /// <summary>
    /// The deadline of one operation: <paramref name="declared"/> by its scenario, else the lock-bound
    /// default max(3 x TIMEOUT, 15 s). Either is kept 5 s below the runner's anonymous no-progress
    /// watchdog, so the attributable DEADLINE failure is recorded instead of a bare HANG.
    /// </summary>
    internal TimeSpan Deadline(TimeSpan? declared)
    {
        var cap = _context.HangTimeout > TimeSpan.FromSeconds(10) ? _context.HangTimeout - TimeSpan.FromSeconds(5) : (TimeSpan?)null;
        var deadline = DeadlineWatchdog.Capped(declared ?? DeadlineWatchdog.LockBound(this.PragmaTimeout, this.DeadlineFloor), cap);
        lock (_writeLock) if (deadline > this.LongestDeadline) this.LongestDeadline = deadline;
        return deadline;
    }

    internal DeadlineWatchdog Watchdog
    {
        get
        {
            lock (_writeLock) return _watchdog ??= new DeadlineWatchdog(this.OnOverdue);
        }
    }

    /// <summary>Start observing Shared core lifecycles and mutex releases for the rest of the run.</summary>
    internal OwnershipMonitor WatchOwnership()
    {
        lock (_writeLock) return _ownership ??= new OwnershipMonitor();
    }

    /// <summary>Fail the run with the first ownership violation latched since the last call.</summary>
    internal void ThrowLatched()
    {
        if (_ownership == null || !_ownership.TryTake(out var violation)) return;
        throw new FuzzFailureException($"OWNERSHIP_{FuzzOracles.Safe(this.Target)}_{violation.Kind}", violation.Detail);
    }

    internal void WriteOutcome(string op, string dimension, int step, string outcome, Exception error, double elapsedMs) =>
        this.Write(OutcomesFile, new
        {
            target = _context.Target, step, op, dimension = dimension ?? "", outcome,
            exceptionType = error?.GetType().FullName, errorCode = (error as LiteException)?.ErrorCode,
            elapsedMs = Math.Round(elapsedMs, 3)
        });

    internal void WriteConnectionClean(string op, ConnectionCleanResult result) =>
        this.Write(ConnectionCleanFile, new
        {
            target = _context.Target, step = _context.Steps, op, mode = result.Mode, clean = result.Clean,
            coreOpen = result.CoreOpen, liveCores = result.LiveCores, mutexSnapshots = result.MutexSnapshots,
            pinActive = result.PinActive, ownershipHeld = result.OwnershipHeld, holderThread = result.HolderThread,
            admittedCalls = result.AdmittedCalls, databaseUsers = result.DatabaseUsers,
            transferredReaders = result.TransferredReaders, openTransactions = result.OpenTransactions,
            engineDisposed = result.EngineDisposed, waitedMs = Math.Round(result.WaitedMs, 3), violations = result.Violations
        });

    internal void WriteQuiescent(string point, QuiescentResult result) =>
        this.Write(QuiescentFile, new
        {
            target = _context.Target, step = _context.Steps, point, path = Path.GetFileName(result.Path),
            clean = result.Clean, threads = result.Threads, threadNames = result.ThreadNames,
            openFds = result.OpenFds, handles = result.Handles.Select(Path.GetFileName).ToArray(),
            mutexFree = result.MutexFree, turnstileFree = result.TurnstileFree, mutexAbandoned = result.MutexAbandoned,
            readerRegistry = result.ReaderRegistry, scratch = result.Scratch, companions = result.Companions,
            waitedMs = Math.Round(result.WaitedMs, 3), gaps = result.Gaps, violations = result.Violations
        });

    internal void WriteFault(string fault, string op, bool fired, FaultDisposition? declared, FaultDisposition? observed) =>
        this.Write(FaultsFile, new
        {
            target = _context.Target, step = _context.Steps, fault, op, fired,
            declared = declared?.ToString(), observed = observed?.ToString()
        });

    private void Write(string file, object record)
    {
        var line = System.Text.Json.JsonSerializer.Serialize(record);
        lock (_writeLock)
        {
            if (_disposed) return;
            if (!_writers.TryGetValue(file, out var writer))
                _writers[file] = writer = new StreamWriter(Path.Combine(_context.DirectoryPath, file), false) { AutoFlush = true, NewLine = "\n" };
            writer.WriteLine(line);
        }
    }

    private void OnMarker(string marker)
    {
        if (marker.StartsWith("refusal:", StringComparison.Ordinal)) _refusalsOnThread++;
    }

    private void OnOverdue(InFlightOperation overdue, InFlightOperation[] inFlight) =>
        FuzzDeadlineFailure.Report(_context, this, overdue, inFlight);

    public void Dispose()
    {
        Reachability.Observer = null;
        _watchdog?.Dispose();
        _ownership?.Dispose();
        lock (_writeLock)
        {
            _disposed = true;
            foreach (var writer in _writers.Values) writer.Dispose();
        }
    }
}
