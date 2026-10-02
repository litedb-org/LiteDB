using System.Diagnostics;
using System.Text.Json;
using LiteDB.Tests.Safety;

namespace LiteDB.Fuzz.Targets;

/// <summary>
/// One <c>shared-contention</c> writer process (<c>--child shared-contention</c>). It runs its seed's
/// script on one Shared connection and applies the invariant oracles itself, because the child has
/// no <see cref="FuzzContext"/>: every operation runs under a <see cref="DeadlineWatchdog"/> deadline,
/// the <see cref="OwnershipMonitor"/> is installed before the connection opens and is checked after
/// every operation, a <see cref="DurableLedger"/> of its own acknowledged commits and known rollbacks
/// is verified on a fresh connection at its end, and each dispose is followed by
/// <see cref="ConnectionCleanProbe"/>. A violation is written to <c>worker-k.failure.json</c> and the
/// process exits non-zero (3: overdue operation; 4: oracle violation; 1: unexpected exception); the
/// parent turns that into the failure id.
/// </summary>
internal sealed class SharedContentionChild : IDisposable
{
    internal const string Target = "shared-contention";
    internal const int OracleExitCode = 4;
    // Declared, not derived from TIMEOUT: waiting for the parent's start signal and the final
    // fresh-connection verification are harness steps, not lock waits.
    internal static readonly TimeSpan StartDeadline = TimeSpan.FromSeconds(60);

    private readonly FuzzOptions _options;
    private readonly DeadlineWatchdog _watchdog;
    private readonly OwnershipMonitor _ownership;
    private readonly DurableLedger _durable = new();
    private readonly Dictionary<int, BsonDocument> _model = new();
    private readonly object _failureLock = new();
    private TimeSpan _lockBound = DeadlineWatchdog.LockBound(SharedContentionFuzzer.Timeout);
    private readonly string _dimension;
    private int _step;
    // Between BeginTrans returning and Commit/Rollback returning; read by the watchdog thread.
    private volatile bool _ownsWriter;
    private int _acquisitions, _commits, _rollbacks, _autoWrites, _reads, _ownershipChecks;

    private SharedContentionChild(FuzzOptions options)
    {
        _options = options;
        _dimension = $"mode=shared;worker={options.WorkerId}";
        _watchdog = new DeadlineWatchdog(this.OnOverdue);
        // Installed before the connection opens, so every core lifecycle and release is observed.
        _ownership = new OwnershipMonitor();
    }

    internal static int Run(FuzzOptions options)
    {
        using var child = new SharedContentionChild(options);
        try { return child.Execute(); }
        catch (ChildFailure failure) { return child.Fail(failure.FailureId, failure.Message, failure.ExitCode); }
        catch (Exception error)
        {
            return child.Fail($"SHARED_CONTENTION_CHILD_{FuzzOracles.Safe(error.GetType().Name)}", error.ToString(), 1);
        }
    }

    private int Execute()
    {
        var script = SharedContentionScript.Generate(_options.Seed, _options.WorkerId, _options.Count);
        var ledger = _options.Ledger;
        var shared = SharedContentionFuzzer.OpenEngine(_options.Database);
        var db = new LiteDatabase(shared);
        using (db)
        {
            var rows = db.GetCollection(SharedContentionScript.Collection);
            var timeout = this.Run("Pragma", () => db.Timeout);
            _lockBound = DeadlineWatchdog.LockBound(timeout);
            File.WriteAllText(SharedContentionScript.Ready(ledger), timeout.TotalMilliseconds.ToString("F0"));
            this.Run("AwaitStart", () =>
            {
                while (!File.Exists(SharedContentionScript.Go(ledger))) Thread.Sleep(1);
                return true;
            }, StartDeadline);

            for (_step = 0; _step < script.Count; _step++)
            {
                var step = script[_step];
                if (step.Kind == ContentionStep.Transaction) this.Transaction(db, rows, step, ledger);
                else if (step.Kind == ContentionStep.Auto) this.AutoWrite(rows, step.Writes[0], ledger);
                else this.Read(rows, step.ReadId);
                this.CheckOwnership(shared, step.Kind);
            }
        }
        this.ConnectionClean(db, "Dispose");

        // A fresh connection must show every acknowledged effect of this worker and no rolled-back one.
        _step = script.Count;
        var fresh = new LiteDatabase(SharedContentionFuzzer.OpenEngine(_options.Database));
        using (fresh)
        {
            var violations = this.Run("FreshVerify", () => _durable.Verify(fresh));
            if (violations.Count > 0)
                throw new ChildFailure($"DURABLE_SHARED_CONTENTION_{violations[0].Split(':')[0]}",
                    $"Worker {_options.WorkerId} on a fresh connection: " + string.Join("; ", violations.Take(5)), OracleExitCode);
        }
        this.ConnectionClean(fresh, "FreshDispose");
        this.TakeLatched("after FreshDispose");

        File.WriteAllText(SharedContentionScript.Result(ledger), System.Text.Json.JsonSerializer.Serialize(new
        {
            worker = _options.WorkerId, seed = _options.Seed, steps = script.Count, acquisitions = _acquisitions,
            commits = _commits, rollbacks = _rollbacks, autoWrites = _autoWrites, reads = _reads,
            ownershipChecks = _ownershipChecks, stopwatchFrequency = Stopwatch.Frequency,
            lockBoundMs = _lockBound.TotalMilliseconds
        }));
        return 0;
    }

    private void Transaction(LiteDatabase db, ILiteCollection<BsonDocument> rows, ContentionStep step, string ledger)
    {
        // Arrive just before BeginTrans; acquired just after it returned (Shared BeginTrans returns only
        // once this connection owns the native writer mutex); released just after Commit/Rollback returned.
        var arrive = Stopwatch.GetTimestamp();
        if (!this.Run("BeginTrans", db.BeginTrans))
            throw new ChildFailure("SHARED_CONTENTION_BEGIN_TRANS_JOINED", "BeginTrans joined a transaction that should not exist.", OracleExitCode);
        var acquired = Stopwatch.GetTimestamp();
        _ownsWriter = true;
        _acquisitions++;
        foreach (var write in step.Writes)
        {
            if (write.Delete) this.Run("Delete", () => rows.Delete(write.Id));
            else this.Run("Upsert", () => rows.Upsert(SharedContentionScript.Document(write.Id, write.Value, _options.WorkerId)));
        }
        var completed = step.Rollback ? this.Run("Rollback", db.Rollback) : this.Run("Commit", db.Commit);
        var released = Stopwatch.GetTimestamp();
        _ownsWriter = false;
        if (!completed)
            throw new ChildFailure("SHARED_CONTENTION_COMPLETION_FALSE",
                $"{(step.Rollback ? "Rollback" : "Commit")} of an open transaction returned false.", OracleExitCode);
        File.AppendAllText(SharedContentionScript.Timing(ledger), System.Text.Json.JsonSerializer.Serialize(
            new ContentionTimingLine(_step, step.Rollback, arrive, acquired, released)) + "\n");
        foreach (var write in step.Writes)
        {
            if (step.Rollback) this.Abort(write.Id, ledger);
            else this.Acknowledge(write, ledger);
        }
        if (step.Rollback) _rollbacks++;
        else _commits++;
    }

    private void AutoWrite(ILiteCollection<BsonDocument> rows, ContentionWrite write, string ledger)
    {
        this.Run("AutoUpsert", () => rows.Upsert(SharedContentionScript.Document(write.Id, write.Value, _options.WorkerId)));
        this.Acknowledge(write, ledger);
        _autoWrites++;
    }

    private void Read(ILiteCollection<BsonDocument> rows, int id)
    {
        var actual = this.Run("FindById", () => rows.FindById(id));
        _model.TryGetValue(id, out var expected);
        _reads++;
        // Only this worker writes its id range, so its own acknowledged state is what any read returns.
        if (expected == null ? actual != null : actual == null || actual["value"] != expected["value"])
            throw new ChildFailure("SHARED_CONTENTION_READ_YOUR_WRITES",
                $"Worker {_options.WorkerId} read {id} as {(actual == null ? "absent" : actual["value"].ToString())}, " +
                $"acknowledged {(expected == null ? "absent" : expected["value"].ToString())}.", OracleExitCode);
    }

    private void Acknowledge(ContentionWrite write, string ledger)
    {
        var document = write.Delete ? null : SharedContentionScript.Document(write.Id, write.Value, _options.WorkerId);
        _durable.Acknowledge(SharedContentionScript.Collection, write.Id, document);
        if (document == null) _model.Remove(write.Id);
        else _model[write.Id] = document;
        AppendLedger(ledger, new ContentionLedgerLine(write.Delete ? "delete" : "upsert", write.Id, write.Value));
    }

    private void Abort(int id, string ledger)
    {
        // A rolled-back write leaves the acknowledged state (DurableLedger ignores ids acknowledged earlier).
        _durable.Abort(SharedContentionScript.Collection, id, null);
        AppendLedger(ledger, new ContentionLedgerLine("rollback", id, 0));
    }

    private static void AppendLedger(string ledger, ContentionLedgerLine line) =>
        File.AppendAllText(ledger, System.Text.Json.JsonSerializer.Serialize(line) + "\n");

    private void CheckOwnership(SharedEngine shared, string op)
    {
        this.TakeLatched("after " + op);
        _ownershipChecks++;
        var violation = _ownership.Evaluate(shared);
        if (violation != null)
            throw new ChildFailure($"OWNERSHIP_SHARED_CONTENTION_{violation.Kind}", $"After {op}: {violation.Detail}", OracleExitCode);
    }

    private void TakeLatched(string point)
    {
        if (_ownership.TryTake(out var violation))
            throw new ChildFailure($"OWNERSHIP_SHARED_CONTENTION_{violation.Kind}", $"Latched {point}: {violation.Detail}", OracleExitCode);
    }

    private void ConnectionClean(LiteDatabase db, string op)
    {
        var result = ConnectionCleanProbe.Evaluate(db, _ownership);
        if (!result.Clean)
            throw new ChildFailure($"CONNECTION_CLEAN_SHARED_CONTENTION_{FuzzOracles.Safe(result.Violations[0].Split(':')[0])}",
                $"After {op}: " + string.Join("; ", result.Violations), OracleExitCode);
    }

    /// <summary>The call, inline on this thread, under its declared deadline (default: lock-bound).</summary>
    private T Run<T>(string op, Func<T> call, TimeSpan? declared = null)
    {
        var deadline = declared ?? _lockBound;
        var item = _watchdog.Begin(op, _dimension, _step, deadline);
        var clock = Stopwatch.StartNew();
        T result;
        try { result = call(); }
        catch (Exception error)
        {
            _watchdog.End(item);
            if (clock.Elapsed > deadline) throw Late(op, clock.Elapsed, deadline, error);
            throw;
        }
        _watchdog.End(item);
        // An operation that finally returned after its deadline still missed it.
        if (clock.Elapsed > deadline) throw Late(op, clock.Elapsed, deadline, null);
        return result;
    }

    private static ChildFailure Late(string op, TimeSpan elapsed, TimeSpan deadline, Exception error) =>
        new(FuzzDeadlineFailure.FailureId(Target, op),
            $"Operation {op} took {elapsed.TotalSeconds:F1} s, beyond its {deadline.TotalSeconds:F0} s deadline" +
            (error == null ? "." : $", then threw {error.GetType().FullName}: {error.Message}"), FuzzDeadlineFailure.ExitCode);

    /// <summary>Watchdog thread: the operation is still blocked. Record it with everything in flight, then exit 3.</summary>
    private void OnOverdue(InFlightOperation overdue, InFlightOperation[] inFlight)
    {
        var message = $"Operation {overdue.Operation} ({overdue.Dimension}) did not complete or throw within its declared " +
            $"{overdue.Deadline.TotalSeconds:F1} s deadline at step {overdue.Step}.";
        this.WriteFailure(FuzzDeadlineFailure.FailureId(Target, overdue.Operation), message, overdue, inFlight);
        Console.Error.WriteLine(message);
        Console.Error.Flush();
        Environment.Exit(FuzzDeadlineFailure.ExitCode);
    }

    private int Fail(string failureId, string message, int exitCode)
    {
        this.WriteFailure(failureId, message, null, _watchdog.Snapshot());
        Console.Error.WriteLine($"{failureId}: {message}");
        return exitCode;
    }

    private void WriteFailure(string failureId, string message, InFlightOperation overdue, InFlightOperation[] inFlight)
    {
        lock (_failureLock)
        {
            var path = SharedContentionScript.Failure(_options.Ledger);
            // The first failure is the primary one; a later one (for example the main thread unwinding) keeps it.
            if (File.Exists(path)) return;
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new ContentionChildFailure(failureId, message, _options.WorkerId,
                _options.Seed, overdue?.Step ?? _step, overdue?.Operation, Math.Round(overdue?.ElapsedMs ?? 0, 1),
                overdue?.Deadline.TotalMilliseconds ?? 0, _ownsWriter, overdue?.StartedTicks ?? Stopwatch.GetTimestamp(), inFlight.Select(item => (object)new
                {
                    item.Operation, item.Dimension, item.Step, item.ManagedThreadId, item.ThreadName,
                    elapsedMs = Math.Round(item.ElapsedMs, 1), deadlineMs = item.Deadline.TotalMilliseconds
                }).ToArray()), new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        _ownership.Dispose();
    }

    private sealed class ChildFailure : Exception
    {
        internal ChildFailure(string failureId, string message, int exitCode) : base(message)
        {
            FailureId = failureId;
            ExitCode = exitCode;
        }

        internal string FailureId { get; }
        internal int ExitCode { get; }
    }
}
